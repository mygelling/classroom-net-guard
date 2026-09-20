import socket, struct, time

srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(('0.0.0.0', 19999))
srv.listen(1)
c, _ = srv.accept()

def readn(n):
    b = b''
    while len(b) < n:
        d = c.recv(n - len(b))
        if not d:
            return None
        b += d
    return b

def read_frame():
    lb = readn(4)
    if not lb:
        return None
    ln = struct.unpack('>I', lb)[0]
    return readn(ln)

def write_frame(s):
    b = s.encode('utf-8')
    c.sendall(struct.pack('>I', len(b)) + b)

hello = read_frame()
policy = '{"t":"policy","data":{"version":99,"classroomOn":false,"allowDomains":["127.0.0.1"],"deviceModes":null,"devicePasswords":null,"unlockPassword":"","download":{"enabled":false,"allowTypes":[],"maxSizeMB":0},"updatedAt":"2026-09-20T12:00:00+08:00"}}'
write_frame(policy)
time.sleep(60)
