#!/usr/bin/env python3
"""Il2cppConsoleMod 交互式 C# 控制台客户端。

用法:
    python3 console.py <host> <token> [--port PORT]

示例:
    python3 console.py 127.0.0.1 9f8e7d6c5b4a3c2d1e0f --port 18765

进入后像普通 shell 一样逐句提交 C# 代码，变量跨提交持久:
    >>> var x = 1;
    >>> x + 1
    = 2
    >>> !reset      # 清空会话状态
    >>> !help       # 帮助
    >>> exit        # 退出 (Ctrl-D 也可以)

方向键可用（readline）：上下键翻历史，左右键移动光标；
历史记录保存在 ~/.il2cpp_console_history。

多行输入：括号未闭合、字符串未闭合或行尾是运算符时自动进入续行（... 提示符），
空行提交整个缓冲区。
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

try:
    import readline  # 上下左右键编辑与历史（Unix 自带；Windows 需 pyreadline3）
except ImportError:
    readline = None

DEFAULT_PORT = 18765
TIMEOUT = 61  # 略大于服务端 60 秒主线程超时
HISTORY_FILE = os.path.expanduser("~/.il2cpp_console_history")


def post(base_url: str, token: str, path: str, body: str = "") -> dict:
    req = urllib.request.Request(
        base_url + path,
        data=body.encode("utf-8"),
        method="POST",
        headers={"Content-Type": "text/plain", "X-Debug-Token": token},
    )
    with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _is_incomplete(code: str) -> bool:
    """粗略判断 C# 代码是否未完成（忽略字符串/注释内的字符）。

    未完成的情形：括号未闭合、字符串字面量未闭合、行尾是运算符/逗号/点等。
    """
    depth = 0
    last_sig = ""
    in_string = None
    escaped = False
    in_comment = False
    i = 0
    n = len(code)
    while i < n:
        c = code[i]
        if in_comment:
            if c == "\n":
                in_comment = False
            i += 1
            continue
        if in_string:
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == in_string:
                in_string = None
            i += 1
            continue
        if c in ('"', "'"):
            in_string = c
            i += 1
            continue
        if c == "/" and i + 1 < n and code[i + 1] == "/":
            in_comment = True
            i += 2
            continue
        if c in "([{":
            depth += 1
            last_sig = c
        elif c in ")]}":
            depth -= 1
            last_sig = c
        elif not c.isspace():
            last_sig = c
        i += 1

    if depth > 0 or in_string is not None:
        return True
    if code.rstrip().endswith("=>"):
        return True
    return bool(last_sig) and last_sig in "=+-*/%&|,?:."


def read_submission():
    """读取一次提交：支持多行续行；返回 None 表示 EOF（退出）。"""
    lines = []
    while True:
        try:
            line = input("... " if lines else ">>> ")
        except EOFError:
            print()
            return None
        except KeyboardInterrupt:
            print("^C")
            lines.clear()
            continue

        line = line.rstrip()
        if not line:
            if lines:
                return "\n".join(lines)  # 续行时空行提交整个缓冲区
            continue

        lines.append(line)
        if not _is_incomplete("\n".join(lines)):
            return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Il2cppConsoleMod 交互式 C# 控制台")
    parser.add_argument("host", help="mod 所在游戏的 IP，例如 127.0.0.1")
    parser.add_argument("token", help="游戏启动日志中打印的 X-Debug-Token")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"端口（默认 {DEFAULT_PORT}）")
    args = parser.parse_args()

    if readline is not None:
        try:
            readline.read_history_file(HISTORY_FILE)
        except Exception:
            pass

    base_url = f"http://{args.host}:{args.port}"
    print(f"Il2cppConsoleMod shell @ {base_url}  (exit 退出 | !reset 清空会话 | !help 帮助)")

    while True:
        code = read_submission()
        if code is None:
            break
        if not code:
            continue
        if code in ("exit", "quit"):
            break
        if code == "!help":
            print("逐句提交 C# 代码；变量跨提交持久，结尾不带分号的表达式自动返回其值。")
            print("预置 using: System / System.Linq / System.Collections.Generic / "
                  "System.Text / System.Threading.Tasks / UnityEngine")
            print("多行输入：括号未闭合或行尾是运算符时自动续行，空行提交整个缓冲区。")
            print("命令: exit/quit 退出 | !reset 清空会话 | !help 帮助")
            continue
        if code == "!reset":
            try:
                print(post(base_url, args.token, "/script/reset"))
            except Exception as ex:
                print(f"reset 失败: {ex}")
            continue

        try:
            result = post(base_url, args.token, "/script", code)
        except urllib.error.HTTPError as ex:
            try:
                detail = json.loads(ex.read().decode("utf-8")).get("error", ex.reason)
            except Exception:
                detail = ex.reason
            print(f"HTTP {ex.code}: {detail}")
            if ex.code == 401:
                print("token 无效，请用游戏日志中打印的新 token 重新启动本客户端。")
            continue
        except Exception as ex:
            print(f"连接失败: {ex}")
            continue

        if not result.get("Success"):
            print(result.get("Error") or "执行失败")
        else:
            if result.get("Output"):
                print(result["Output"], end="" if result["Output"].endswith("\n") else "\n")
            if result.get("HasResult"):
                print(f"= {result['Result']}")

    if readline is not None:
        try:
            readline.write_history_file(HISTORY_FILE)
        except Exception:
            pass

    return 0


if __name__ == "__main__":
    sys.exit(main())
