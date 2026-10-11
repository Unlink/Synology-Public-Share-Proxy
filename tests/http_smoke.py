"""Exercise the real HTTP routes with synthetic local QA data; no DSM or third-party calls."""
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.parse

root = Path(__file__).resolve().parents[1]
with socket.socket() as s:
    s.bind(('127.0.0.1', 0))
    port = s.getsockname()[1]
origin = f'http://127.0.0.1:{port}'
env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', Proxy__DemoMode='true',
           Proxy__PublicOrigin=origin, Proxy__MaxUploadBytes='128')
process = subprocess.Popen(['dotnet', 'run', '--no-build', '--configuration', 'Release', '--no-launch-profile',
                            '--project', str(root / 'src/ShareProxy'), '--urls', origin],
                           cwd=root, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
cookie = ''
checks = 0
def request(method, path, data=None, headers=None):
    connection = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
    h = {'Cookie': cookie, **(headers or {})}
    connection.request(method, path, data, h)
    response = connection.getresponse()
    result = response.status, dict(response.getheaders()), response.read()
    connection.close()
    return result
def check(value, label):
    global checks
    assert value, label
    checks += 1
try:
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError('Test server exited before readiness.')
        try:
            if request('GET', '/healthz')[0] == 200:
                break
        except OSError:
            time.sleep(.1)
    else:
        raise RuntimeError('Test server did not start.')
    status, headers, body = request('GET', '/api/session')
    check(status == 200, 'session')
    cookie = headers['Set-Cookie'].split(';', 1)[0]
    csrf = json.loads(body)['csrf']
    check('httponly' in headers['Set-Cookie'].lower() and 'samesite=strict' in headers['Set-Cookie'].lower(), 'cookie policy')
    check(headers['Cache-Control'] == 'no-store', 'no caching')
    check("frame-ancestors 'none'" in headers['Content-Security-Policy'], 'CSP')
    check(headers['X-Content-Type-Options'] == 'nosniff', 'content sniffing disabled')
    check(headers['Referrer-Policy'] == 'strict-origin', 'referrer never contains share token')
    check(request('GET', '/api/shares/demo-share')[0] == 200, 'metadata')
    check(len(json.loads(request('GET', '/api/shares/demo-share/files')[2])['files']) == 6, 'list')
    check(request('GET', '/api/shares/demo-password')[0] == 401, 'password required')
    check(request('GET', '/api/shares/demo-expired')[0] == 410, 'expiry')
    for path in ['/webapi/auth.cgi', '/webman/index.cgi', '/api/proxy', '/appsettings.json', '/admin', '/src/ShareProxy/Program.cs']:
        check(request('GET', path)[0] == 404, f'default deny: {path}')
    payload = json.dumps({'password': 'atelier'})
    check(request('POST', '/api/shares/demo-password/unlock', payload, {'Origin': origin, 'Content-Type': 'application/json'})[0] == 403, 'missing CSRF')
    check(request('POST', '/api/shares/demo-password/unlock', payload, {'Origin': 'https://evil.example', 'X-Share-CSRF': csrf, 'Content-Type': 'application/json'})[0] == 403, 'cross origin rejected')
    check(request('POST', '/api/shares/demo-password/unlock', payload, {'Origin': origin, 'X-Share-CSRF': csrf, 'Content-Type': 'application/json'})[0] == 204, 'unlock')
    check(request('GET', '/api/shares/demo-password')[0] == 200, 'authorized metadata')
    upload_headers = {'Origin': origin, 'X-Share-CSRF': csrf, 'Content-Type': 'application/octet-stream', 'X-File-Name': 'qa.txt', 'X-Uploader-Name': 'QA'}
    check(request('POST', '/api/shares/demo-upload/upload', b'qa-bytes', upload_headers)[0] == 200, 'raw streaming upload')
    check(request('POST', '/api/shares/demo-share/upload', b'qa-bytes', upload_headers)[0] == 403, 'upload to download share denied')
    check(request('POST', '/api/shares/demo-upload/upload', b'x' * 129, upload_headers)[0] == 413, 'upload size enforcement')
    check(request('POST', '/api/shares/demo-upload/upload', b'x', {**upload_headers, 'X-File-Name': '..%2Fprivate'})[0] == 400, 'path traversal rejected')
    check(request('GET', '/', headers={'Host': 'evil.example'})[0] == 400, 'Host enforcement')
    encoded = urllib.parse.urlencode({'csrf': csrf, 'ids': json.dumps(['demo_file_brief_0001'])})
    status, headers, body = request('POST', '/api/shares/demo-share/download', encoded, {'Origin': origin, 'Content-Type': 'application/x-www-form-urlencoded'})
    check(status == 200 and headers['Content-Disposition'].startswith('attachment'), 'native download attachment')
    check("frame-src 'self'" in headers['Content-Security-Policy'] and "frame-ancestors 'self'" in headers['Content-Security-Policy'] and headers['X-Frame-Options'] == 'SAMEORIGIN', 'download frame policy')
    status, headers, body = request('POST', '/api/shares/demo-expired/download', encoded, {'Origin': origin, 'Content-Type': 'application/x-www-form-urlencoded'})
    check(status == 410 and headers['X-Frame-Options'] == 'SAMEORIGIN' and json.loads(body)['code'] == 'unavailable', 'download error available to same-origin frame')
    for _ in range(7):
        last = request('POST', '/api/shares/demo-password/unlock', json.dumps({'password': 'wrong'}), {'Origin': origin, 'X-Share-CSRF': csrf, 'Content-Type': 'application/json'})
    check(last[0] == 429 and last[1].get('Retry-After') == '60', 'password rate limit')
    print(f'HTTP smoke: {checks} checks passed (synthetic data, no DSM).')
finally:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
