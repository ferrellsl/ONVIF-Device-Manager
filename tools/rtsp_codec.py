"""Show which codecs an RTSP camera stream uses (from its DESCRIBE answer).

Usage:  python rtsp_codec.py rtsp://192.168.2.58:554/11
Asks for the camera's user name and password (the password is not shown or stored).
"""
import getpass
import hashlib
import re
import socket
import sys
from urllib.parse import urlsplit


def describe(sock, url, cseq, auth=None):
    request = 'DESCRIBE %s RTSP/1.0\r\nCSeq: %d\r\nAccept: application/sdp\r\nUser-Agent: rtsp_codec\r\n' % (url, cseq)
    if auth:
        request += 'Authorization: %s\r\n' % auth
    sock.sendall((request + '\r\n').encode())
    data = b''
    while b'\r\n\r\n' not in data:
        chunk = sock.recv(4096)
        if not chunk:
            break
        data += chunk
    head, _, body = data.partition(b'\r\n\r\n')
    head = head.decode(errors='replace')
    length = re.search(r'(?im)^Content-Length:\s*(\d+)', head)
    if length:
        while len(body) < int(length.group(1)):
            chunk = sock.recv(4096)
            if not chunk:
                break
            body += chunk
    return head, body.decode(errors='replace')


def main():
    url = sys.argv[1] if len(sys.argv) > 1 else input('RTSP address: ').strip()
    parts = urlsplit(url)
    sock = socket.create_connection((parts.hostname, parts.port or 554), 5)
    sock.settimeout(5)
    head, body = describe(sock, url, 1)
    if ' 401 ' in head.split('\r\n')[0]:
        user = input('User name: ')
        password = getpass.getpass('Password: ')
        challenge = re.search(r'(?im)^WWW-Authenticate:\s*(\w+)\s*(.*)$', head)
        scheme, params = challenge.group(1), challenge.group(2)
        if scheme.lower() == 'digest':
            fields = dict(re.findall(r'(\w+)="?([^",]*)"?', params))
            md5 = lambda s: hashlib.md5(s.encode()).hexdigest()
            ha1 = md5('%s:%s:%s' % (user, fields['realm'], password))
            ha2 = md5('DESCRIBE:%s' % url)
            response = md5('%s:%s:%s' % (ha1, fields['nonce'], ha2))
            auth = 'Digest username="%s", realm="%s", nonce="%s", uri="%s", response="%s"' % (
                user, fields['realm'], fields['nonce'], url, response)
        else:
            import base64
            auth = 'Basic ' + base64.b64encode(('%s:%s' % (user, password)).encode()).decode()
        head, body = describe(sock, url, 2, auth)
    print(head.split('\r\n')[0])
    for line in body.splitlines():
        if line.startswith(('m=', 'a=rtpmap', 'a=framesize', 'a=x-dimensions')):
            print('  ' + line)


main()
