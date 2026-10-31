"""Agent behavior observation script (dev tool, not part of the runtime).

Two modes:

  smoke   In-process component checks against the code ON DISK plus the live
          8001 service. No cloud LLM is called, no tokens are consumed.
          Verifies, in order:
            1. MCP enumeration works (anyio / sys.path fix) and catalog tiers
            2. discover_tools returns non-empty parameter signatures
               (_describe_params fix)
            3. invoke_tool dispatches async-only MCP tools without the
               "StructuredTool does not support sync invocation" error
               (ainvoke fix)
            4. C# IR paging (network_start / network_count) is honored by the
               RUNNING C# app, and reports fidelity.truncated / network counts
          Any check that fails prints the reason instead of raising.

  stream  End-to-end run against the RUNNING 8000 backend: POST /generate_stream
          and print a timestamped SSE timeline (thinking / tool_call / content /
          token_usage / plan_progress), first-content latency, an event
          histogram and a final-answer preview. Consumes real cloud tokens.
          The API key must be supplied via --api-key or TIA_TEST_API_KEY.
          Optionally --block <name> fetches that block's IR from 8001 first and
          attaches it as context, mimicking the C# "Add To AI Context" flow.

Usage (embedded python):
  TIAAgent\\python\\python.exe observe_agent.py smoke [--block 考试]
  TIAAgent\\python\\python.exe observe_agent.py stream --block 考试 --prompt 解析这个fc块
"""

import argparse
import asyncio
import json
import os
import sys
import time

_BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
if _BACKEND_DIR not in sys.path:
    sys.path.insert(0, _BACKEND_DIR)
_PYDEPS = os.path.join(_BACKEND_DIR, "_pydeps")
if os.path.isdir(_PYDEPS):
    import site
    site.addsitedir(_PYDEPS)
    if _PYDEPS in sys.path:
        sys.path.remove(_PYDEPS)
    sys.path.insert(0, _PYDEPS)

TIA_CTX = os.getenv("TIA_CTX_BASE", "http://127.0.0.1:8001")
BACKEND = os.getenv("TIA_BACKEND_BASE", "http://127.0.0.1:8000")


def _hr(title):
    print("\n" + "=" * 72)
    print(title)
    print("=" * 72)


def _load_ai_config():
    """Read the C# host's cloud relay config (%APPDATA%\\TIAOpennessStudio\\ai_config.json).

    Returns (endpoint, api_key, model); empty strings when unavailable. Lets the
    stream mode reuse the exact deepseek-v4-pro relay the app itself uses,
    without hardcoding secrets anywhere.
    """
    path = os.path.join(os.environ.get("APPDATA", ""), "TIAOpennessStudio", "ai_config.json")
    try:
        with open(path, encoding="utf-8") as f:
            cfg = json.load(f)
        return (str(cfg.get("Endpoint") or ""), str(cfg.get("ApiKey") or ""),
                str(cfg.get("Model") or cfg.get("SelectedModel") or ""))
    except Exception as e:
        print(f"  (ai_config.json unreadable: {e})")
        return "", "", ""


def _ir_stats(ir_text):
    """Parse an IR JSON string; return (networks, truncated, first_idx, err).

    Handles both shapes:
    * get_block_content envelope: {format, revision, ..., content: <IR>}
    * canonical IR: {kind, language, networks[], fidelity{...}}
    """
    try:
        ir = json.loads(ir_text)
    except Exception as e:
        return None, None, None, f"IR not JSON: {e}"
    if not isinstance(ir, dict):
        return None, None, None, "IR not an object"
    low = {k.lower(): v for k, v in ir.items()}
    content = low.get("content")
    if isinstance(content, dict):
        ir = content
        low = {k.lower(): v for k, v in ir.items()}
    canonical = low.get("canonical") or {}
    if isinstance(canonical, dict):
        canonical = {k.lower(): v for k, v in canonical.items()}
    fidelity = low.get("fidelity") or {}
    if isinstance(fidelity, dict):
        fidelity = {k.lower(): v for k, v in fidelity.items()}
    nets = low.get("networks") or canonical.get("networks") or []
    trunc = fidelity.get("truncated")
    first = None
    if nets:
        n0 = nets[0]
        if isinstance(n0, dict):
            n0 = {k.lower(): v for k, v in n0.items()}
            first = n0.get("order", n0.get("networkindex"))
    return len(nets), trunc, first, None


async def smoke(block):
    import httpx
    from tools.catalog import build_catalog, get_catalog
    from tools.meta_tools import build_discover_tool, build_invoke_tool

    _hr("[1] catalog build (MCP enumeration over 8001)")
    cat = await build_catalog(force=True)
    tiers = {}
    for e in cat.values():
        tiers[e.tier] = tiers.get(e.tier, 0) + 1
    mcp_ro = [e.name for e in cat.values() if e.tier == "mcp" and e.access == "read"]
    print(f"  catalog tools: {len(cat)}  tiers={tiers}")
    print(f"  mcp read-only: {len(mcp_ro)} -> {mcp_ro[:8]}{' ...' if len(mcp_ro) > 8 else ''}")
    mcp_ok = bool(mcp_ro)
    print(f"  => MCP enumeration: {'OK' if mcp_ok else 'FAILED (anyio/path fix not effective?)'}")

    _hr("[2] discover_tools parameter signatures (reviewer role)")
    disc = build_discover_tool("reviewer")
    out = disc.invoke({"query": ""})
    # Entry format: "- name [cat]\n    desc 参数(...)" — params live on the
    # indented second line, so pair each "- " line with its continuation.
    entries = []
    cur = None
    for l in out.splitlines():
        if l.startswith("- "):
            cur = [l, ""]
            entries.append(cur)
        elif cur is not None and l.strip():
            cur[1] += l
    with_params = [e for e in entries if "参数(" in e[1]]
    print(f"  reviewer tools listed: {len(entries)}, with params: {len(with_params)}")
    for e in with_params[:6]:
        print("   ", e[0].strip(), "|", e[1].strip()[:80])
    # Cross-check against the catalog: a tool shows params iff its schema has
    # properties. Zero-arg tools (get_program_tree, list_block_groups, ...) are legitimately
    # param-less, so only a catalog/shown MISMATCH is a real defect.
    mismatches = []
    for e in entries:
        name = e[0].split()[1]
        entry = cat.get(name)
        expected = bool(entry and entry.params)
        shown = "参数(" in e[1]
        if expected != shown:
            mismatches.append(name)
    noarg = [e[0].split()[1] for e in entries if "参数(" not in e[1]]
    print(f"  zero-arg tools (legitimately param-less): {noarg}")
    params_ok = not mismatches
    print(f"  => params fix: {'OK' if params_ok else 'MISMATCH: ' + str(mismatches[:6])}")

    _hr("[3] invoke_tool dispatch (sync vs async tools)")
    inv = build_invoke_tool("reviewer")
    # When MCP is up the REST duplicates are deduped away, so probe whichever
    # tier actually exists in the catalog.
    db_name = "get_db_structure" if "get_db_structure" in cat else "get_db_structure_rest"
    # MCP and REST copies may name the arg differently (name vs db_name);
    # derive the key from the catalog signature so the probe sends a valid arg.
    db_key = "name" if "name:" in (cat.get(db_name).params if db_name in cat else "") else "db_name"
    r = await inv.ainvoke({"name": db_name,
                           "args_json": json.dumps({db_key: "__probe_nonexistent__"})})
    sync_err = "sync invocation" in r
    print(f"  {db_name} ({cat[db_name].tier if db_name in cat else '?'}) -> {r[:90]!r}")
    if "get_block_content" in cat:
        ir_name = "get_block_content"
        ir_args = {"block_name": block, "format": "ir"}
    elif "export_block_ir" in cat:
        ir_name = "export_block_ir"
        ir_args = {"block_name": block}
    else:
        ir_name = "export_block_ir_rest"
        ir_args = {"block_name": block}
    r2 = await inv.ainvoke({"name": ir_name, "args_json": json.dumps(ir_args)})
    sync_err = sync_err or ("sync invocation" in r2)
    n, trunc, first, err = _ir_stats(r2)
    print(f"  IR tool ({ir_name}) -> networks={n} truncated={trunc} first_idx={first} err={err or r2[:60]!r}")
    print(f"  => async dispatch: {'OK (no sync-invocation error)' if not sync_err else 'FAILED'}")

    _hr("[4] C# IR paging probe (network_start=4, network_count=2)")
    paging_args = dict(ir_args, network_start=4, network_count=2)
    r3 = await inv.ainvoke({"name": ir_name, "args_json": json.dumps(paging_args)})
    n3, trunc3, first3, err3 = _ir_stats(r3)
    if err3:
        print(f"  paging probe error: {err3} / {r3[:120]!r}")
    elif first3 == 4 and (n3 or 0) <= 2:
        print(f"  slice networks={n3} first_idx={first3} truncated={trunc3}")
        print("  => C# paging: ACTIVE (new exe running)")
    else:
        print(f"  slice networks={n3} first_idx={first3} (expected first_idx=4)")
        print("  => C# paging: IGNORED (running C# app is the OLD exe; restart it)")

    _hr("smoke verdict")
    print(f"  MCP enumeration : {'OK' if mcp_ok else 'FAIL'}")
    print(f"  params visible  : {'OK' if params_ok else 'FAIL'}")
    print(f"  async dispatch  : {'OK' if not sync_err else 'FAIL'}")
    print(f"  full IR load    : networks={n} truncated={trunc}")
    print(f"  C# paging       : {'OK' if (not err3 and first3 == 4) else 'STALE/FAIL'}")


def stream(args):
    import httpx

    api_key = args.api_key or os.getenv("TIA_TEST_API_KEY", "")
    endpoint, cfg_key, cfg_model = _load_ai_config()
    api_key = api_key or cfg_key
    base_url = args.base_url or endpoint or "https://inferaiapi.com/v1"
    model = args.model or cfg_model or "deepseek-v4-pro"
    if not api_key:
        print("stream mode needs an API key: --api-key ... / env TIA_TEST_API_KEY / ai_config.json")
        return 2
    print(f"  relay config: base_url={base_url} model={model} key=...{api_key[-4:]}")

    context = ""
    if args.context_file:
        with open(args.context_file, encoding="utf-8") as f:
            context = f.read()
    elif args.block:
        # Use the POST tool endpoint: its body path is UTF-8-safe on the C# side,
        # while GET query decoding is only fixed in exes rebuilt after the
        # GetQueryValue UTF-8 change (older running apps mojibake Chinese names).
        with httpx.Client(timeout=60) as c:
            r = c.post(f"{TIA_CTX}/api/tool/export_block",
                       json={"block_name": args.block, "format": "ir"})
            data = r.json()
            if not data.get("ok"):
                print(f"context fetch failed: {data.get('error')}")
                return 2
            context = f"Attached block IR ({args.block}):\n```json\n{data.get('result','')}\n```"
            print(f"context attached from 8001: {len(context)} chars")

    body = {
        "api_key": api_key,
        "model": model,
        "provider": "openai_compat",
        "base_url": base_url,
        "prompt": args.prompt,
        "context": context,
        "intent": args.intent,
        "stream": True,
    }

    _hr(f"stream: intent={args.intent} model={model} prompt={args.prompt[:40]!r}")
    t0 = time.perf_counter()
    hist = {}
    content_chars = 0
    first_content_ms = None
    tool_events = []
    content_tail = []

    with httpx.Client(timeout=httpx.Timeout(900, read=900)) as c:
        with c.stream("POST", f"{BACKEND}/generate_stream", json=body) as resp:
            if resp.status_code != 200:
                print(f"HTTP {resp.status_code}: {resp.read()[:300]}")
                return 2
            ev = None
            for line in resp.iter_lines():
                if line.startswith("event: "):
                    ev = line[7:].strip()
                    continue
                if not line.startswith("data: "):
                    continue
                data = line[6:]
                hist[ev] = hist.get(ev, 0) + 1
                ts = time.perf_counter() - t0
                if ev == "thinking":
                    pass  # too verbose; counted only
                elif ev == "content":
                    if first_content_ms is None:
                        first_content_ms = int(ts * 1000)
                    try:
                        piece = json.loads(data)
                    except Exception:
                        piece = data
                    content_chars += len(piece)
                    content_tail.append(piece)
                    if content_chars <= 200 or content_chars % 500 < 16:
                        print(f"  [{ts:7.1f}s] content +{len(piece)} (total {content_chars})")
                elif ev == "tool_call":
                    try:
                        tj = json.loads(data)
                        tool_events.append((ts, tj.get("name"), tj.get("status"),
                                            str(tj.get("output") or tj.get("input"))[:80]))
                        print(f"  [{ts:7.1f}s] tool {tj.get('status')}: {tj.get('name')} "
                              f"{str(tj.get('output') or tj.get('input'))[:80]!r}")
                    except Exception:
                        print(f"  [{ts:7.1f}s] tool_call raw: {data[:120]}")
                elif ev == "error":
                    print(f"  [{ts:7.1f}s] ERROR: {data[:300]}")
                elif ev in ("token_usage", "plan_progress"):
                    print(f"  [{ts:7.1f}s] {ev}: {data[:160]}")

    total = time.perf_counter() - t0
    _hr("stream summary")
    print(f"  total_s={total:.1f}  first_content_ms={first_content_ms}")
    print(f"  event histogram: {hist}")
    print(f"  thinking chunks: {hist.get('thinking', 0)}  content chars: {content_chars}")
    print(f"  tool calls: {len(tool_events)}")
    for ts, name, status, preview in tool_events:
        print(f"    - {name} [{status}] {preview!r}")
    tail = "".join(content_tail)[-600:]
    print(f"  final content tail:\n{tail}")
    if hist.get("thinking", 0) > 50 and content_chars < 50:
        print("  ⚠ pattern: heavy thinking, near-empty content (reasoning-model loop risk)")
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="mode", required=True)
    s = sub.add_parser("smoke")
    s.add_argument("--block", default="考试")
    t = sub.add_parser("stream")
    t.add_argument("--prompt", default="解析这个fc块")
    t.add_argument("--intent", default="ExplainCode")
    t.add_argument("--model", default="")
    t.add_argument("--base-url", default="")
    t.add_argument("--api-key", default="")
    t.add_argument("--block", default="")
    t.add_argument("--context-file", default="")
    args = p.parse_args()
    if args.mode == "smoke":
        return asyncio.run(smoke(args.block))
    return stream(args)


if __name__ == "__main__":
    sys.exit(main())
