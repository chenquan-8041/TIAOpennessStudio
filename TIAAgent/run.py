"""Entry point for the TIA AI Code Generator backend server.

Usage:
    python run.py
    python run.py --port 8000
    python run.py --host 0.0.0.0 --port 8000
"""

import sys
import os

# Ensure the TIAAgent directory is on sys.path for package imports
_backend_dir = os.path.dirname(os.path.abspath(__file__))
if _backend_dir not in sys.path:
    sys.path.insert(0, _backend_dir)

# The embedded Python runs in ._pth mode, which ignores PYTHONPATH, so packages that
# only live in TIAAgent/_pydeps (langchain-mcp-adapters, mcp, pywin32) were never importable
# and the agent silently ran without MCP tools.
#
# _pydeps MUST take precedence over the embedded Lib/site-packages: that dir ships older
# anyio/langchain_core that break mcp 1.30+ (anyio.create_memory_object_stream[...] subscript
# raises "'function' object is not subscriptable" on anyio<4). We still call addsitedir first so
# _pydeps/pywin32.pth is processed (mcp needs `import pywintypes` on Windows), then hoist
# _pydeps_dir to the head of sys.path.
_pydeps_dir = os.path.join(_backend_dir, "_pydeps")
if os.path.isdir(_pydeps_dir):
    import site
    site.addsitedir(_pydeps_dir)
    if _pydeps_dir in sys.path:
        sys.path.remove(_pydeps_dir)
    sys.path.insert(0, _pydeps_dir)

import uvicorn


def main():
    host = "127.0.0.1"
    port = 8000

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif args[i] == "--port" and i + 1 < len(args):
            port = int(args[i + 1])
            i += 2
        else:
            i += 1

    uvicorn.run("app.main:app", host=host, port=port, reload=False)


if __name__ == "__main__":
    main()
