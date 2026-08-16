"""Unity Bridge 配置文件读取（Python 侧共用：CLI 与底层客户端都从这里取默认参数）。

配置文件位置：工具根目录下的 bridge.ini（即 <root>/bridge.ini）。
本文件只依赖标准库，不依赖 client/cli，避免循环导入。

支持的段落:
    [server]
    port = 21927          ; TCP 监听/连接端口；C# 服务器与 Python CLI 共用
    [reload]
    timeout = 30          ; bridge.reload 等待编译恢复的超时（秒）
"""

from __future__ import annotations

import configparser
from pathlib import Path

# 本文件位于 <root>/python/unity_bridge/config.py
_BRIDGE_ROOT = Path(__file__).resolve().parents[2]
_INI_PATH = _BRIDGE_ROOT / "bridge.ini"

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 21927
DEFAULT_RELOAD_TIMEOUT = 30.0


def load_server_port(default: int = DEFAULT_PORT) -> int:
    """读取 bridge.ini 的 [server] port，作为 CLI --port 与底层客户端的默认端口。

    文件缺失、段落/键不存在或解析失败时回退到 default（21927）。命令行 --port
    显式传入时仍会覆盖此值。
    """
    parser = configparser.ConfigParser(inline_comment_prefixes=(";", "#"))
    try:
        if parser.read(_INI_PATH, encoding="utf-8"):
            if parser.has_option("server", "port"):
                return int(parser.get("server", "port"))
    except (configparser.Error, ValueError, OSError):
        pass
    return default


def load_reload_timeout(default: float = DEFAULT_RELOAD_TIMEOUT) -> float:
    """读取 bridge.ini 的 [reload] timeout，作为 bridge.reload 等待超时的默认值。

    文件缺失、段落/键不存在或解析失败时回退到 default（30 秒）。命令行 --timeout
    显式传入时仍会覆盖此值。
    """
    parser = configparser.ConfigParser(inline_comment_prefixes=(";", "#"))
    try:
        if parser.read(_INI_PATH, encoding="utf-8"):
            if parser.has_option("reload", "timeout"):
                return float(parser.get("reload", "timeout"))
    except (configparser.Error, ValueError, OSError):
        pass
    return default
