"""通过 stdio 对 Unity MCP 适配器执行端到端冒烟测试。"""

from __future__ import annotations

import asyncio
import json
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run() -> None:
    server = StdioServerParameters(
        command=sys.executable,
        args=["-m", "unity_bridge.mcp_server"],
    )
    async with stdio_client(server) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            tools = await session.list_tools()
            names = {tool.name for tool in tools.tools}
            expected = {"unity_ping", "unity_version", "get_scene_tree"}
            missing = expected - names
            if missing:
                raise AssertionError(f"MCP tools missing: {sorted(missing)}")

            ping = await session.call_tool("unity_ping")
            if ping.isError:
                raise AssertionError(f"unity_ping failed: {ping.content}")
            payload = json.loads(ping.content[0].text)
            if payload.get("pong") is not True:
                raise AssertionError(f"unexpected ping result: {payload}")

            tree = await session.call_tool("get_scene_tree", {"depth": 1})
            if tree.isError:
                raise AssertionError(f"get_scene_tree failed: {tree.content}")
            scene = json.loads(tree.content[0].text)
            if scene.get("type") != "scene":
                raise AssertionError(f"unexpected scene result: {scene}")

            print(
                json.dumps(
                    {
                        "toolCount": len(names),
                        "pong": payload["pong"],
                        "scene": scene.get("name"),
                        "rootCount": scene.get("rootCount"),
                    },
                    ensure_ascii=False,
                )
            )


if __name__ == "__main__":
    asyncio.run(run())
