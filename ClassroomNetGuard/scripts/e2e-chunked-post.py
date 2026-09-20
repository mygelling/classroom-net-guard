import http.server, socketserver, time, sys

BODY_FILE = r'C:\ProgramData\NetGuard\logs\e2e-post-body.txt'

class H(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def do_GET(self):
        if self.path == '/chunked':
            body1 = b'hello-chunk-1|'
            body2 = b'world-chunk-2|'
            body3 = b'end'
            head = b'HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n'
            chunk1 = hex(len(body1))[2:].encode() + b'\r\n' + body1 + b'\r\n'
            # 响应头 + 第一个 chunk 行 + 数据一次写入：让代理读响应头时 rest 里已含 chunk 数据
            self.wfile.write(head + chunk1)
            self.wfile.flush()
            time.sleep(0.2)
            self.wfile.write(hex(len(body2))[2:].encode() + b'\r\n' + body2 + b'\r\n')
            self.wfile.flush()
            time.sleep(0.2)
            self.wfile.write(hex(len(body3))[2:].encode() + b'\r\n' + body3 + b'\r\n')
            self.wfile.flush()
            time.sleep(0.2)
            self.wfile.write(b'0\r\n\r\n')
            self.wfile.flush()
            return
        self.send_response(200)
        self.send_header('Content-Length', '6')
        self.end_headers()
        self.wfile.write(b'get-ok')
    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0) or 0)
        body = self.rfile.read(n)
        with open(BODY_FILE, 'wb') as f:
            f.write(body)
        resp = b'{"ok":true,"len":%d}' % len(body)
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(resp)))
        self.end_headers()
        self.wfile.write(resp)
    def log_message(self, *a):
        pass

socketserver.ThreadingTCPServer.allow_reuse_address = True
with socketserver.ThreadingTCPServer(('127.0.0.1', 18080), H) as s:
    s.serve_forever()
