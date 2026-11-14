"""imsg-web の API の試しのサーバー（本物の Mac が無くても、アプリの見た目と送るところを試す）。

    python tools/mock_imsg.py [port]

http://127.0.0.1:8799 で待つ。トークンは "test"。会話は作りもので、送った文は会話に足す。
アプリの設定の URL に http://127.0.0.1:8799、トークンに test を入れる（http はこの PC の中だけアプリが許す）。
"""
import json
import struct
import sys
import zlib
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, unquote

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8799
EPOCH = "e1"


def png(w, h, color):
    raw = b"".join(b"\x00" + bytes(color) * w for _ in range(h))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


now = datetime.now(timezone.utc)
def ago(minutes):
    return (now - timedelta(minutes=minutes)).isoformat().replace("+00:00", "Z")

def msg(i, text, mine, minutes, sender=None, attachments=None, avatar=None, reactions=None):
    return {"id": f"m{i}", "text": text, "isFromMe": mine, "sender": sender, "avatarId": avatar, "attachments": attachments or [],
            "link": None, "replyTo": None, "reactions": reactions or [], "createdAt": ago(minutes), "trimmed": False}

HISTORY = {
    "c1": [
        msg(1, "今日の夜ってひま？", False, 60 * 26),
        msg(2, "ひまだよ、どうしたの", True, 60 * 26 - 3),
        msg(3, "駅前に新しいラーメン屋ができたから行かない？", False, 50),
        msg(4, "", False, 49, attachments=[{"id": "a1", "kind": "image", "sticker": False, "preview": False}]),
        msg(5, "いいね！19 時に駅の改札でどう", True, 45, reactions=[{"emoji": "👍", "kind": "like", "senders": [], "fromMe": False, "count": 1}]),
        msg(6, "了解、また連絡する", False, 3),
    ],
    "c2": [
        msg(11, "週末のキャンプ、持ち物の一覧を送ります", False, 60 * 30, sender="たけし", avatar="f2"),
        msg(12, "テントは僕が持っていきます", False, 60 * 29, sender="みさき"),
        msg(13, "ありがとう！じゃあ火の道具は持っていくね", True, 60 * 28),
    ],
    "c3": [msg(21, "荷物を受け取りました。ありがとうございました", False, 60 * 24 * 3)],
}
CHATS = [
    {"id": "c1", "name": "ゆうき", "service": "iMessage", "isGroup": False, "unreadCount": 1, "faces": ["f1"]},
    {"id": "c2", "name": "キャンプの会", "service": "iMessage", "isGroup": True, "unreadCount": 0, "faces": ["f2", None]},
    {"id": "c3", "name": "+81 90-1234-5678", "service": "SMS", "isGroup": False, "unreadCount": 0, "faces": []},
]
PICTURES = {"f1": png(96, 96, (240, 160, 90)), "f2": png(96, 96, (90, 170, 240)), "a1": png(480, 320, (120, 200, 140))}


def chat_view(c):
    h = HISTORY[c["id"]]
    last = h[-1]
    return {**c, "lastMessageAt": last["createdAt"], "trimmed": False,
            "preview": {"text": last["text"] or "写真", "trimmed": False, "fromMe": last["isFromMe"]}}


class Handler(BaseHTTPRequestHandler):
    def reply(self, code, body, kind="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def authorized(self):
        if self.headers.get("Origin") or self.headers.get("Cookie"):
            self.reply(403, {"code": "CLIENT_FROM_BROWSER"})
            return False
        if self.headers.get("Authorization") != "Bearer test":
            self.reply(401, {"code": "UNAUTHORIZED"})
            return False
        return True

    def do_GET(self):
        if not self.authorized():
            return
        path = urlparse(self.path).path
        parts = [unquote(p) for p in path.strip("/").split("/")]
        if path == "/api/capabilities":
            return self.reply(200, {"epoch": EPOCH, "mode": "readonly", "features": {"send": {"state": "available", "reasonCode": "SEND_READY"}}})
        if path == "/api/chats":
            chats = sorted((chat_view(c) for c in CHATS), key=lambda c: c["lastMessageAt"], reverse=True)
            return self.reply(200, {"epoch": EPOCH, "chats": chats, "limit": 50})
        if len(parts) == 4 and parts[:2] == ["api", "chats"] and parts[3] == "messages":
            if parts[2] not in HISTORY:
                return self.reply(409, {"code": "STALE_CHAT"})
            return self.reply(200, {"epoch": EPOCH, "messages": HISTORY[parts[2]], "limit": 50})
        if len(parts) == 3 and parts[1] in ("attachments", "avatars") and parts[2] in PICTURES:
            return self.reply(200, PICTURES[parts[2]], "image/png")
        self.reply(404, {"code": "NOT_FOUND"})

    def do_POST(self):
        if not self.authorized():
            return
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        if self.path == "/api/send":
            chat = body.get("chatId")
            if chat not in HISTORY:
                return self.reply(409, {"code": "STALE_CHAT"})
            HISTORY[chat].append({**msg(100 + len(HISTORY[chat]), body.get("text", ""), True, 0), "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")})
            print("送った:", chat, body.get("text"), flush=True)
            return self.reply(200, {"state": "sent"})
        self.reply(404, {"code": "NOT_FOUND"})

    def log_message(self, *args):
        pass


print(f"http://127.0.0.1:{PORT}", flush=True)
ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
