"""
Openness sample retrieval tool — lets the agent discover and read real Openness
implementation samples from this project.

Samples live in tools/openness_samples/*.md (see the README in that directory).
Adding a sample only requires dropping a .md file into the directory; this tool
enumerates them automatically, no code change needed.

Modes (for the LLM):
  list_openness_samples()  → "here are the available samples"
  get_openness_sample(topic) → "here is how this is implemented" (reads real code anchors)
"""

import os
from langchain_core.tools import tool

_SAMPLES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "openness_samples")


def _list_topics():
    if not os.path.isdir(_SAMPLES_DIR):
        return []
    return sorted(
        os.path.splitext(f)[0]
        for f in os.listdir(_SAMPLES_DIR)
        if f.endswith(".md") and f.lower() != "readme.md"
    )


@tool
def list_openness_samples() -> str:
    """List the available TIA Openness implementation sample topics.

    These samples are extracted from verified, runnable C# implementations in
    this project (connect / locate DLL / compile, etc.) and serve as accurate
    references when writing Openness code. Use get_openness_sample(topic) to read
    a specific one.
    """
    topics = _list_topics()
    if not topics:
        return "No Openness samples available."
    return "Available Openness implementation samples (read one with get_openness_sample(topic)):\n" + "\n".join(
        f"- {t}" for t in topics
    )


@tool
def get_openness_sample(topic: str) -> str:
    """Read the full content of a specified Openness implementation sample
    (real code + key facts).

    Args:
        topic: Sample topic name (from list_openness_samples, e.g. "resolve_dll_path").

    Returns:
        The sample markdown content; a list of available topics when not found.
    """
    name = (topic or "").strip()
    if not name:
        return list_openness_samples.invoke({})
    # Accept the topic with or without the .md suffix
    if name.endswith(".md"):
        name = name[:-3]
    path = os.path.join(_SAMPLES_DIR, name + ".md")
    if not os.path.isfile(path):
        topics = _list_topics()
        return f"Sample '{topic}' not found. Available topics: {', '.join(topics) if topics else '(none)'}"
    try:
        with open(path, "r", encoding="utf-8") as f:
            return f.read()
    except Exception as e:
        return f"Failed to read sample: {e}"


OPENNESS_SAMPLE_TOOLS = [list_openness_samples, get_openness_sample]
