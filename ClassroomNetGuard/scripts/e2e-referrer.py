# -*- coding: utf-8 -*-
"""E2E: 资源放行（白名单页面引用第三方资源）。
- 假教师端 19999：classroomOn=true, allowDomains=["127.0.0.1:18080"], allowResourceDomains=["127.0.0.1:18081"], referrerAllowEnabled=true
- 上游 18080/18081/18082 三个 HTTP 服务
- 客户端断言 6 个场景：白名单/资源列表直连放行、第三方无来源拦截、Referer/Origin 关联放行、非法来源拦截
"""
import json, socket, struct, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ---------- 上游 ----------
class UpHandler(BaseHTTPRequestHandler):
    tag = "up"
    def do_GET(self):
        body = ("OK-" + self.tag).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *a): pass

class Up18080(UpHandler): tag = "main"
class Up18081(UpHandler): tag = "cdn"
class Up18082(UpHandler): tag = "third"

# ---------- 假教师端 ----------
def frame(obj):
    b = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    return struct.pack(">I", len(b)) + b

def teacher_server(stop):
    policy = {
        "t": "policy",
        "data": {
            "version": 99,
            "classroomOn": True,
            "allowDomains": ["127.0.0.1:18080"],
            "allowResourceDomains": ["127.0.0.1:18081"],
            "referrerAllowEnabled": True,
            "deviceModes": None,
            "devicePasswords": None,
            "unlockPassword": "",
            "download": {"enabled": False, "allowTypes": [], "maxSizeMB": 0},
            "updatedAt": "2026-09-21T00:00:00"
        }
    }
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", 19999)); srv.listen(4); srv.settimeout(0.5)
    while not stop.is_set():
        try: c, _ = srv.accept()
        except socket.timeout: continue
        except Exception: break
        def handle(conn):
            try:
                conn.settimeout(8)
                h = conn.recv(4)
                if len(h) < 4: return
                n = struct.unpack(">I", h)[0]
                data = b""
                while len(data) < n:
                    d = conn.recv(n - len(data))
                    if not d: return
                    data += d
                print("teacher got:", data[:200])
                conn.sendall(frame(policy))
                time.sleep(30)
            except Exception: pass
            finally:
                try: conn.close()
                except: pass
        threading.Thread(target=handle, args=(c,), daemon=True).start()
    srv.close()

# ---------- 客户端 ----------
def raw_req(port, path, referer=None, origin=None, host=None):
    s = socket.create_connection(("127.0.0.1", 8888), timeout=8)
    h = (host or "127.0.0.1:%d" % port)
    req = "GET http://%s%s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n" % (h, path, h)
    if referer: req += "Referer: %s\r\n" % referer
    if origin: req += "Origin: %s\r\n" % origin
    req += "\r\n"
    s.sendall(req.encode())
    out = b""
    while True:
        try: d = s.recv(65536)
        except socket.timeout: break
        if not d: break
        out += d
    s.close()
    first = out.split(b"\r\n", 1)[0].decode(errors="replace")
    return first

def check(name, got, want):
    ok = (want in got)
    print(("PASS" if ok else "FAIL"), "|", name, "->", got)
    return ok

def main():
    stop = threading.Event()
    servers = []
    for cls in (Up18080, Up18081, Up18082):
        srv = ThreadingHTTPServer(("127.0.0.1", int(cls.tag)) if False else
                                  (("127.0.0.1", {"Up18080":18080,"Up18081":18081,"Up18082":18082}[cls.__name__])), cls)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        servers.append(srv)
    t = threading.Thread(target=teacher_server, args=(stop,), daemon=True); t.start()
    time.sleep(1)

    results = []
    results.append(check("白名单站点直连(18080)", raw_req(18080, "/"), "200"))
    results.append(check("资源放行域名直连(18081)", raw_req(18081, "/"), "200"))
    results.append(check("第三方无来源(18082)", raw_req(18082, "/"), "403"))
    results.append(check("第三方+白名单Referer(18082)", raw_req(18082, "/", referer="http://127.0.0.1:18080/page"), "200"))
    results.append(check("第三方+资源域名Referer(18082)", raw_req(18082, "/", referer="http://127.0.0.1:18081/x.js"), "200"))
    results.append(check("第三方+非法Referer(18082)", raw_req(18082, "/", referer="http://evil.com/"), "403"))
    results.append(check("第三方+白名单Origin(18082)", raw_req(18082, "/", origin="http://127.0.0.1:18080"), "200"))

    stop.set()
    for srv in servers: srv.shutdown()
    print("ALL PASS" if all(results) else "SOME FAILED")

if __name__ == "__main__":
    main()
