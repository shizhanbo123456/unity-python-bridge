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
            expected = {
                "unity_ping",
                "unity_version",
                "list_unity_commands",
                "call_unity_command",
                "reload_unity",
                "get_scene_tree",
            }
            missing = expected - names
            if missing:
                raise AssertionError(f"MCP tools missing: {sorted(missing)}")

            ping = await session.call_tool("unity_ping")
            if ping.isError:
                raise AssertionError(f"unity_ping failed: {ping.content}")
            payload = json.loads(ping.content[0].text)
            if payload.get("pong") is not True:
                raise AssertionError(f"unexpected ping result: {payload}")

            catalog_result = await session.call_tool("list_unity_commands")
            if catalog_result.isError:
                raise AssertionError(f"list_unity_commands failed: {catalog_result.content}")
            catalog = json.loads(catalog_result.content[0].text)
            if catalog.get("count", 0) < 1:
                raise AssertionError(f"unexpected command catalog: {catalog}")

            generic = await session.call_tool(
                "call_unity_command",
                {"command": "bridge.version", "arguments": {}},
            )
            if generic.isError:
                raise AssertionError(f"generic bridge.version failed: {generic.content}")
            version = json.loads(generic.content[0].text)

            guarded = await session.call_tool(
                "call_unity_command",
                {"command": "debug.log", "arguments": {"message": "blocked"}},
            )
            if not guarded.isError:
                raise AssertionError("mutating generic command was not guarded")

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
                        "bridgeCommandCount": catalog["count"],
                        "bridgeVersion": version.get("version"),
                        "pong": payload["pong"],
                        "scene": scene.get("name"),
                        "rootCount": scene.get("rootCount"),
                    },
                    ensure_ascii=False,
                )
            )


if __name__ == "__main__":
    asyncio.run(run())
