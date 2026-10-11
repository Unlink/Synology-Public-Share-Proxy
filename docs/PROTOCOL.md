# DSM wire protocol and evidence

Reviewed privately: the anonymous single-file and File Request captures attached to the referenced conversation; the folder capture supplied during this task; and the two password-protected download/upload captures subsequently supplied from Downloads. Raw captures, share tokens, user names, paths, ciphertext, cookies and session IDs were excluded from project files and test fixtures. All examples below are synthetic.

## Closed upstream operation set

| Purpose | Wire request | Evidence |
| --- | --- | --- |
| Determine protection | GET `/sharing/{token}`; read only `SYNO.Core.Sharing.Session` script URL | All captures |
| Public session metadata | GET `/sharing/webapi/entry.cgi`, `SYNO.Core.Sharing.Session/get/v1`, JSON-string `sharing_id`, `sharing_status` | All captures |
| Validate share | POST `/sharing/webapi/entry.cgi`, `SYNO.Core.Sharing.Initdata/get/v1`; `X-SYNO-SHARING: token` | All captures |
| Obtain password envelope key | POST `/sharing/webapi/entry.cgi/SYNO.API.Encryption`, `SYNO.API.Encryption/getinfo/v1`, `format=module` | Password captures |
| Unlock share | POST `/sharing/webapi/entry.cgi/SYNO.Core.Sharing.Login`, `SYNO.Core.Sharing.Login/login/v1`, `__cIpHeRtExT={rsa,aes}` | Password captures, successful `data.sharing_sid` |
| List folder | POST `/sharing/webapi/entry.cgi`, `SYNO.FolderSharing.List/list/v2` | Folder capture, root and child navigation |
| Download selection | POST `/fsdownload/webapi/file_download.cgi/{archive-name}`, `SYNO.FolderSharing.Download/download/v2` | Folder capture, ZIP transfer |
| Download single shared file | GET `/fsdownload/{token}/{filename}` | Download URL construction in captured File Station client; actual transfer not recorded in single-file captures |
| Preflight upload | POST `/sharing/webapi/entry.cgi`, `SYNO.FileStation.CheckPermission/write/v3` | Both upload captures |
| Upload bytes | Multipart POST `/webapi/entry.cgi?api=SYNO.FileStation.Upload&method=upload&version=2&_sharing_id=...` | Both upload captures, successful transfers |

The implementation adds the share header to its fixed upstream requests. It does not copy incoming cookies, forwarded headers, client URLs or arbitrary query fields. It never calls `SYNO.API.Auth`, generic API discovery, DSM account login, file mutation/delete/rename, or Drive APIs.

## Metadata

Session is two JavaScript assignments containing JSON objects: `SYNO.SDS.Session` and `SYNO.SDS.ExtraSession`. The parser reads JSON values with `Utf8JsonReader`, checks assignment delimiters and rejects trailing code. It does not evaluate JavaScript. The Session share ID must match the requested token and `sharing` must be true. DSM account sessions are rejected.

Initdata's `data.Sharing` carries `enabled`, `protect_type`, `app.enable_upload`, `app.is_folder`, `expire_at`, `start_at`, `expire_times` and `enable_match_ip`. Public filenames are in ExtraSession for anonymous shares and `data.Private.filename` after password login. Only public title/description/mode/expiry and minimal file fields leave the backend; DSM configuration, owner data and privilege maps are dropped. Owner-supplied text is inserted with `textContent`, never rendered as HTML.

DSM is the authority for time, expiration, disabled/revoked links and file permissions. Metadata is not cached between operations. Each list/download/upload runs bootstrap, Session and Initdata again; the final native transfer API also checks the share. This repeats DSM validation and may affect visit-count links, so those and IP-bound links are explicitly unsupported.

## Folder listing and selection

Captured parameters: `offset`, `limit`, JSON-string `sort_by="name"`, `sort_direction="ASC"`, `action="enum"`, JSON-array `additional`, `filetype="all"`, JSON-string `folder_path`, JSON-string `_sharing_id`. The initial folder path is `/` plus the public folder name. The capture confirms each returned item's parent equals the requested folder.

The proxy requests only `size,time` additional data, limits pages to 200 and drops owner/permission metadata. Each returned path must be a direct child of the requested folder and within the share root. The client receives a random ID; the server alone remembers its path. Navigation and downloads accept IDs, never raw paths. Root changes clear remembered IDs.

ZIP POST uses JSON-string `mode="download"`, `dlname="shared-files.zip"`, JSON-array `path`, JSON-string `_sharing_id`, `codepage="enu"` and `stdhtml=false`. The folder capture also contains a single-file GET with an opaque `dlink`; that optional mechanism is **not** exposed or implemented. Individual selected files use the documented-in-capture selection POST, producing a ZIP.

## Password envelope and session

Captured Encryption data: `cipherkey`, `ciphertoken`, `public_key` (4096-bit RSA modulus in hex), `server_time`. Captured Sharing.Login uses the dynamic cipher field `__cIpHeRtExT`, with `rsa` and `aes` entries.

The captured `synowebapi` encryption code specifies RSA PKCS#1 v1.5 over a 501-character random passphrase, then CryptoJS's default salted AES passphrase format over URL-encoded parameters. The proxy independently implements the same envelope using .NET cryptography, cryptographically random passphrase/salt, the OpenSSL `Salted__` format, EVP_BytesToKey MD5 compatibility, AES-256-CBC and PKCS#7. The plaintext parameters contain the server timestamp and JSON-string `password` and `sharing_id`. HTTPS remains mandatory; legacy envelope algorithms are protocol compatibility only.

Successful login returns `data.sharing_sid`; the browser then immediately calls Initdata. The provided HAR exports omit Cookie and Set-Cookie fields, so the exact cookie names/lifetime cannot be confirmed from those exports. The proxy uses an isolated standard `CookieContainer` to honor real DSM Set-Cookie responses, keeps the SID private, and requires subsequent DSM validation to succeed. It does not fabricate a login session or fall back to plain password submission. Actual cookie behavior needs the live DSM check listed in DSM-VALIDATION.md.

## Upload

The public API receives one raw stream with Content-Length and validated UTF-8 escaped filename/uploader headers. It translates this into the captured native sequence. Preflight fields use Synology JSON string encoding. The multipart fields `mtime`, `overwrite`, `sharing_id`, `uploader_name`, `size` use raw strings; binary `file` is last. Query `_sharing_id` is JSON encoded.

The proxy uses `overwrite=false`, whereas the capture used `true`: this deliberate safety choice prevents silent overwrite and needs live DSM conflict verification. Neither destination paths nor `create_parents` are accepted. Revalidation and permission checks occur before reading the incoming binary. No IFormFile, full-file buffering, disk spool, extra accounts or duplicate storage is used.

## Unverified variants

Wrong password and expired-link responses were not supplied. Error cases in tests are synthetic; generic rejected API responses fail closed. Account-based, redirected, visitor-count and IP-bound shares fail closed. Partial transfers, zero-byte files, Unicode multipart filenames, DSM restarts, large files, exact cookie behavior and TLS trust installation must be checked on the physical NAS. Unexpected protocol shapes produce a generic error; they do not broaden the upstream operation set.

The [official Synology File Station API guide](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf) was used for background on response envelopes and multipart ordering. Its authenticated API workflow is not used here. Public FolderSharing/Core.Sharing behavior above is based on the supplied captures, not assumed from the authenticated APIs.
