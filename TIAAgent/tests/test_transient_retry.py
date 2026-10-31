"""Provider throttling must be retried, not turned into a failed review."""

import asyncio

import pytest

from agent import supervisor


def test_rate_limit_text_is_transient():
    exc = RuntimeError("站点触发限流，请稍后重试")
    assert supervisor._is_rate_limit_error(exc)
    assert supervisor._is_transient_error(exc)


@pytest.mark.parametrize("message", [
    "Error code: 429 - too many requests",
    "rate limit exceeded",
    "quota exceeded for this project",
])
def test_other_rate_limit_spellings(message):
    assert supervisor._is_transient_error(RuntimeError(message))


def test_permanent_errors_are_not_transient():
    assert not supervisor._is_transient_error(ValueError("bad json"))
    assert not supervisor._is_transient_error(PermissionError("access denied"))


def test_rate_limit_retry_uses_long_backoff(monkeypatch):
    delays = []

    async def fake_sleep(seconds):
        delays.append(seconds)

    monkeypatch.setattr(supervisor.asyncio, "sleep", fake_sleep)

    calls = {"n": 0}

    async def fn():
        calls["n"] += 1
        raise RuntimeError("站点触发限流，请稍后重试")

    with pytest.raises(RuntimeError):
        asyncio.run(supervisor._ainvoke_with_retry(fn, what="TEST"))

    assert calls["n"] == supervisor._MAX_RETRY_ATTEMPTS
    assert delays == [15.0, 30.0]


def test_genuine_error_is_not_retried():
    async def fn():
        raise ValueError("bad json")

    with pytest.raises(ValueError):
        asyncio.run(supervisor._ainvoke_with_retry(fn, what="TEST"))
