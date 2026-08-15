"""UnityPythonBridge - 通过 TCP/JSON 协议在 Unity Editor 运行时执行命令。

纯标准库实现，无第三方依赖。
"""

from .client import UnityClient, UnityBridgeError
from .cli import main

__all__ = ["UnityClient", "UnityBridgeError", "main"]
__version__ = "0.1.0"
