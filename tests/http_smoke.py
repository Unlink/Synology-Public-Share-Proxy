"""Exercise production HTTP routes against a local, synthetic DSM fixture."""
import http.client
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import threading
import time
import urllib.parse

TOKENS = {
    'folder': 'syntheticFolder111', 'single': 'syntheticSingle111',
    'password': 'syntheticPassword111', 'upload': 'syntheticUpload111',
    'expired': 'syntheticExpired111',
}

class DsmFixture(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def respond(self, body, kind='application/json', cookie=None):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(200)
        self.send_header('Content-Type', kind)
        self.send_header('Content-Length', str(len(data)))
        if cookie:
            self.send_header('Set-Cookie', cookie + '; Path=/; HttpOnly')
        if kind == 'application/octet-stream':
            self.send_header('Content-Disposition', 'attachment; filename=fixture.txt')
        self.end_headers()
        self.wfile.write(data)

    def dispatch(self):
        url = urllib.parse.urlsplit(self.path)
        fields = urllib.parse.parse_qs(url.query)
        body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        if self.headers.get('Content-Type', '').startswith('application/x-www-form-urlencoded'):
            fields.update(urllib.parse.parse_qs(body.decode()))
        cookies = SimpleCookie(self.headers.get('Cookie', ''))
        token = self.headers.get('X-SYNO-SHARING') or (cookies['fixture'].value if 'fixture' in cookies else '')
        if url.path.startswith('/sharing/') and url.path.count('/') == 2:
            token = url.path.split('/')[-1]
            if token not in TOKENS.values():
                self.send_error(404)
                return
            status = 'password' if token == TOKENS['password'] else 'none'
            query = urllib.parse.urlencode({'api': 'SYNO.Core.Sharing.Session', 'sharing_id': json.dumps(token), 'sharing_status': json.dumps(status)})
            self.respond('<html><script src="webapi/entry.cgi?' + query.replace('&', '&amp;') + '"></script></html>', 'text/html', 'fixture=' + token)
            return
        api = fields.get('api', [''])[0]
        if api == 'SYNO.Core.Sharing.Session':
            session = {'sharing': True, 'sharing_id': token, 'isLogined': False}
            self.respond('SYNO.SDS.Session = ' + json.dumps(session) + ';SYNO.SDS.ExtraSession = ' + json.dumps({'status': 0, 'filename': 'Project'}) + ';', 'application/javascript')
            return
        if api == 'SYNO.API.Encryption':
            assert url.path == '/sharing/webapi/encryption.cgi/SYNO.API.Encryption'
            assert 'X-SYNO-SHARING' not in self.headers
            self.respond(json.dumps({'success': True, 'data': {'cipherkey': '__cIpHeRtExT', 'ciphertoken': '__fixtureTime', 'public_key': 'f' * 1024, 'server_time': 1234567}}))
            return
        if api == 'SYNO.Core.Sharing.Login':
            assert 'X-SYNO-SHARING' not in self.headers
            assert '__cIpHeRtExT' in fields and 'password' not in fields
            self.respond(json.dumps({'success': True, 'data': {'sharing_sid': 'syntheticSession111'}}), cookie='fixture-unlocked=1')
            return
        if api == 'SYNO.Core.Sharing.Initdata':
            sharing = {'enabled': token != TOKENS['expired'], 'protect_type': 'password' if token == TOKENS['password'] else 'none',
                       'redirect_type': 'none', 'enable_match_ip': True, 'expire_times': 0, 'expire_at': '',
                       'app': {'enable_upload': token == TOKENS['upload'], 'is_folder': token != TOKENS['single'], 'request_name': 'Request', 'request_info': ''}}
            self.respond(json.dumps({'success': True, 'data': {'Sharing': sharing, 'Private': {'filename': 'Project'}}}))
            return
        if api == 'SYNO.FolderSharing.List':
            files = [{'path': '/Project/fixture.txt', 'name': 'fixture.txt', 'isdir': False, 'additional': {'size': 8}}]
            self.respond(json.dumps({'success': True, 'data': {'total': 1, 'files': files}}))
            return
        if api == 'SYNO.FileStation.CheckPermission':
            self.respond('{"success":true,"data":{}}')
            return
        if api == 'SYNO.FileStation.Upload':
            assert fields['_sharing_id'] == [TOKENS['upload']]
            self.respond('{"success":true,"data":{}}')
            return
        if url.path.startswith('/fsdownload/'):
            self.respond(b'fixture-file', 'application/octet-stream')
            return
        self.send_error(404)

    do_GET = dispatch
    do_POST = dispatch

root = Path(__file__).resolve().parents[1]
fixture = ThreadingHTTPServer(('127.0.0.1', 0), DsmFixture)
thread = threading.Thread(target=fixture.serve_forever, daemon=True)
thread.start()
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
origin = f'http://127.0.0.1:{port}'
env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development',
           Proxy__DsmOrigin=f'http://127.0.0.1:{fixture.server_port}',
           Proxy__PublicOrigin=origin, Proxy__MaxUploadBytes='128', Proxy__DiagnosticsVerbose='true',
           Logging__LogLevel__Default='Trace', Logging__LogLevel__Microsoft__AspNetCore='Trace')
logs = tempfile.TemporaryFile(mode='w+', encoding='utf-8')
process = subprocess.Popen(['dotnet', 'run', '--no-build', '--configuration', 'Release', '--no-launch-profile',
                            '--project', str(root / 'src/ShareProxy'), '--urls', origin],
                           cwd=root, env=env, stdout=logs, stderr=subprocess.STDOUT)
cookie = ''
checks = 0

def request(method, path, data=None, headers=None):
    connection = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
    connection.request(method, path, data, {'Cookie': cookie, **(headers or {})})
    response = connection.getresponse()
    result = response.status, dict(response.getheaders()), response.read()
    connection.close()
    return result

def check(value, label):
    global checks
    assert value, label
    checks += 1

def share(kind, suffix=''):
    return '/api/shares/' + TOKENS[kind] + suffix

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
    check(set(json.loads(body)) == {'csrf'}, 'production session schema')
    check('httponly' in headers['Set-Cookie'].lower() and 'samesite=strict' in headers['Set-Cookie'].lower(), 'cookie policy')
    check(headers['Cache-Control'] == 'no-store', 'no caching')
    check("frame-ancestors 'none'" in headers['Content-Security-Policy'], 'CSP')
    check(headers['X-Content-Type-Options'] == 'nosniff', 'content sniffing disabled')
    check(headers['Referrer-Policy'] == 'strict-origin', 'referrer never contains share token')
    check(re.fullmatch('[a-f0-9]{32}', headers['X-Request-ID']) is not None, 'server request ID')
    check(request('GET', share('folder'))[0] == 200, 'metadata')
    single = json.loads(request('GET', share('single'))[2])
    check(single['mode'] == 'download' and len(single['files']) == 1 and not single['files'][0]['isDirectory'], 'single-file metadata')
    files = json.loads(request('GET', share('folder', '/files'))[2])['files']
    check(len(files) == 1, 'list')
    file_id = files[0]['id']
    check(request('GET', share('password'))[0] == 401, 'password required')
    check(request('GET', share('expired'))[0] == 410, 'expiry')
    for path in ['/webapi/auth.cgi', '/webman/index.cgi', '/api/proxy', '/appsettings.json', '/admin', '/src/ShareProxy/Program.cs']:
        check(request('GET', path)[0] == 404, 'default deny')
    payload = json.dumps({'password': 'synthetic-password'})
    unlock_headers = {'Origin': origin, 'X-Share-CSRF': csrf, 'Content-Type': 'application/json'}
    check(request('POST', share('password', '/unlock'), payload, {'Origin': origin, 'Content-Type': 'application/json'})[0] == 403, 'missing CSRF')
    check(request('POST', share('password', '/unlock'), payload, {**unlock_headers, 'Origin': 'https://evil.example'})[0] == 403, 'cross origin rejected')
    check(request('POST', share('password', '/unlock'), '{invalid-json', unlock_headers)[0] == 400, 'invalid JSON rejected')
    check(request('POST', share('password', '/unlock'), '{"password":"x","password":"y"}', unlock_headers)[0] == 400, 'duplicate password rejected')
    check(request('POST', share('password', '/unlock'), '{"password":"x","account":"admin"}', unlock_headers)[0] == 400, 'account field rejected')
    check(request('POST', share('password', '/unlock'), payload, unlock_headers)[0] == 204, 'unlock')
    check(request('GET', share('password'))[0] == 200, 'authorized metadata')
    # A new visitor has no protected DSM session or file handles.
    check(request('GET', share('password'), headers={'Cookie': ''})[0] == 401, 'protected session isolation')
    upload_headers = {'Origin': origin, 'X-Share-CSRF': csrf, 'Content-Type': 'application/octet-stream', 'X-File-Name': 'fixture.txt', 'X-Uploader-Name': 'Fixture'}
    check(request('POST', share('upload', '/upload'), b'fixture', upload_headers)[0] == 200, 'streaming upload')
    check(request('POST', share('folder', '/upload'), b'fixture', upload_headers)[0] == 403, 'upload to download share denied')
    check(request('POST', share('upload', '/upload'), b'x' * 129, upload_headers)[0] == 413, 'upload size enforcement')
    check(request('POST', share('upload', '/upload'), b'x', {**upload_headers, 'X-File-Name': '..%2Fprivate'})[0] == 400, 'path traversal rejected')
    status, headers, body = request('GET', '/', headers={'Host': 'evil.example', 'X-Request-ID': 'callerSecret123'})
    check(status == 400, 'Host enforcement')
    reference = json.loads(body)['requestId']
    check(reference == headers['X-Request-ID'] and reference != 'callerSecret123', 'server-generated error reference')
    post_headers = {'Origin': origin, 'Content-Type': 'application/x-www-form-urlencoded'}
    encoded = urllib.parse.urlencode({'csrf': csrf, 'ids': json.dumps([file_id])})
    status, headers, body = request('POST', share('folder', '/download'), encoded, post_headers)
    check(status == 200 and headers['Content-Disposition'].startswith('attachment') and body == b'fixture-file', 'native download attachment')
    check('shared-files.zip' not in headers['Content-Disposition'], 'one file is not zipped')
    check("frame-ancestors 'self'" in headers['Content-Security-Policy'] and headers['X-Frame-Options'] == 'SAMEORIGIN', 'download frame policy')
    receipt = 'a' * 32
    encoded_receipt = urllib.parse.urlencode({'csrf': csrf, 'ids': json.dumps([file_id]), 'receipt': receipt})
    status, headers, body = request('POST', share('folder', '/download'), encoded_receipt, post_headers)
    check(status == 200 and headers.get('Set-Cookie', '').startswith('download-ready-' + receipt + '=1;'), 'download readiness')
    check('samesite=strict' in headers['Set-Cookie'].lower() and 'max-age=120' in headers['Set-Cookie'].lower() and 'httponly' not in headers['Set-Cookie'].lower(), 'readiness cookie policy')
    status, headers, body = request('POST', share('expired', '/download'), encoded_receipt, post_headers)
    check(status == 410 and 'download-ready-' not in headers.get('Set-Cookie', ''), 'error never signals readiness')
    bad_receipt = urllib.parse.urlencode({'csrf': csrf, 'ids': json.dumps([file_id]), 'receipt': 'bad_cookie;injection'})
    check(request('POST', share('folder', '/download'), bad_receipt, post_headers)[0] == 400, 'invalid marker rejected')
    check(request('POST', share('folder', '/download'), encoded + '&csrf=duplicate', post_headers)[0] == 400, 'duplicate form rejected')
    check(request('POST', share('folder', '/download'), encoded + '&path=/private', post_headers)[0] == 400, 'raw path rejected')
    null_id = urllib.parse.urlencode({'csrf': csrf, 'ids': '[null]'})
    check(request('POST', share('folder', '/download'), null_id, post_headers)[0] == 400, 'null ID rejected')
    check(request('POST', share('folder', '/download'), encoded, {**post_headers, 'Content-Type': 'application/x-www-form-urlencoded-evil'})[0] == 415, 'content type prefix rejected')
    forged = urllib.parse.urlencode({'csrf': csrf, 'ids': '["forgedFileId12345678"]'})
    check(request('POST', share('folder', '/download'), forged, post_headers)[0] == 404, 'forged handle rejected')
    for _ in range(7):
        last = request('POST', share('password', '/unlock'), payload, unlock_headers)
    check(last[0] == 429 and last[1].get('Retry-After') == '60', 'password rate limit')
    process.terminate()
    process.wait(timeout=10)
    logs.seek(0)
    captured = logs.read()
    check('Proxy failure request=' + reference in captured, 'reference matches safe console log')
    secrets = [*TOKENS.values(), 'synthetic-password', 'callerSecret123', 'fixture.txt', csrf, cookie.split('=', 1)[1]]
    check(all(secret not in captured for secret in secrets), 'console never logs secrets')
    check('Microsoft.AspNetCore' not in captured and '/api/shares/' not in captured, 'framework URL logs disabled at Trace')
    print(f'HTTP smoke: {checks} checks passed (local synthetic DSM fixture).')
finally:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    logs.close()
    fixture.shutdown()
    fixture.server_close()
