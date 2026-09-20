import socket, time, sys

def raw_request(path, method='GET', body=None, body_splits=None):
    s = socket.create_connection(('127.0.0.1', 8888), timeout=10)
    head = "%s http://127.0.0.1:18080%s HTTP/1.1\r\nHost: 127.0.0.1:18080\r\n" % (method, path)
    if body is not None:
        head += "Content-Length: %d\r\n" % len(body)
        head += "Content-Type: application/json\r\n"
    head += "Connection: close\r\n\r\n"
    s.sendall(head.encode('ascii'))
    if body is not None:
        if body_splits:
            s.sendall(body[:body_splits[0]])
            time.sleep(0.3)
            s.sendall(body[body_splits[0]:])
        else:
            s.sendall(body)
    out = b''
    while True:
        try:
            d = s.recv(65536)
        except Exception as e:
            out += b'<<recv-err:%s>>' % str(e).encode()
            break
        if not d:
            break
        out += d
    s.close()
    return out

print("=== GET /chunked raw bytes ===")
r = raw_request('/chunked')
print("total len:", len(r))
print(r.hex())
idx = r.find(b'\r\n\r\n')
body = r[idx+4:] if idx >= 0 else r
print("body hex:", body.hex())
print("body text:", body)
print("=== GET /chunked dechunked check ===")
# simple parse of raw chunked from body after \r\n\r\n
idx = r.find(b'\r\n\r\n')
body = r[idx+4:] if idx >= 0 else r
# decode chunks
decoded = b''
p = 0
while p < len(body):
    e = body.find(b'\r\n', p)
    if e < 0: break
    size_line = body[p:e].decode('ascii', 'ignore').split(';')[0].strip()
    try:
        size = int(size_line, 16)
    except Exception:
        print("size parse fail at", repr(body[p:e]), "remain", len(body)-p)
        break
    p = e + 2
    if size == 0:
        break
    decoded += body[p:p+size]
    p += size + 2
print("decoded:", decoded)
print("match:", decoded == b'hello-chunk-1|world-chunk-2|end')

print("=== POST /echo body split 49+12 ===")
body = b'{"user":"zhangsan","pwd":"1234567890abcdef","data":"x" * 500}'
print("body len:", len(body))
r = raw_request('/echo', 'POST', body, body_splits=[49])
print(r[:300])
