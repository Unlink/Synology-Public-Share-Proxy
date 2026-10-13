# Public API

Only these routes are exposed. Other routes return 404, including DSM paths.

| Method | Route | Request / response |
| --- | --- | --- |
| GET | `/healthz` | `{status:"ok"}`; local process health, no DSM probe |
| GET | `/`, `/sharing/{token}`, `/s/{token}` | Own static UI |
| GET | `/app.css`, `/app.js`, `/icon.svg` | Own assets |
| GET | `/api/session` | HttpOnly visitor cookie and `{csrf}` |
| GET | `/api/shares/{token}` | `{title,description,mode,expiresAt,passwordProtected,maxUploadBytes,files}` |
| POST | `/api/shares/{token}/unlock` | JSON `{password}`; `X-Share-CSRF`; 204 after DSM authorizes the share |
| GET | `/api/shares/{token}/files?folder={id}&offset=0` | `{files,offset,total}`; optional folder ID, 200-item pages |
| POST | `/api/shares/{token}/download` | URL-encoded `csrf`, `ids` (JSON array), optional `receipt` (32 lowercase hex characters); native attachment stream |
| POST | `/api/shares/{token}/upload` | Raw `application/octet-stream`, Content-Length, `X-Share-CSRF`, `X-File-Name`, `X-Uploader-Name` |

Filename and uploader headers are `encodeURIComponent` strings, decoded once on the server. No arbitrary URL, host, API, SID, path or credentials are accepted. Unlock JSON must contain exactly one string property named `password`; duplicate or unknown properties are rejected. Download form fields must be unique and limited to `csrf`, `ids` and `receipt`. `ids` must contain 1–100 distinct session-local IDs. POST Origin must equal the configured public origin. Host is checked except the fixed health endpoint. The visitor cookie is bound to one in-memory session, with independent upstream connections per share. CSRF tokens from another session are rejected. Session cookies are HttpOnly, SameSite=Strict and Secure with a `__Host-` prefix on HTTPS. No incoming cookie is forwarded to DSM.

File DTO: `{id,name,isDirectory,size,modified}`. `size` is bytes or null; `modified` is Unix seconds or null. `id` is random and session-local; it is not a DSM path. A restart invalidates IDs/cookies. Error body: `{code,requestId}` without upstream details or secrets; server-generated `X-Request-ID` correlates responses with safe console logs. Rate-limit/fallback responses may contain only `{code}`. 401 requests a password; 403 rejects an unsupported/forbidden operation; 410 means unavailable; 413 enforces transfer size; 429 asks the visitor to wait; 502/504 cover upstream/protocol failure. Some expired states cannot be distinguished from removed links and intentionally share the unavailable message.

Downloads use a native form/iframe so large binaries do not accumulate in the browser heap. Errors before streaming are JSON and shown by the UI from the same-origin iframe. Errors after streaming starts abort the response; the browser's download manager reports the interruption. Upload progress means browser-to-proxy bytes; at 100% the UI waits for DSM's success response before displaying completion.

Exactly one selected file is streamed with its original filename. Multiple items or a directory are streamed as a ZIP. All operations revalidate authorization with DSM. Concurrent metadata buffering is limited to eight upstream requests process-wide. Transfers, request sizes, session counts and request rates have separate limits; capacity rejection returns 429 without queueing a transfer. Upload never requests overwrite of an existing file.

A successful attachment response may set `download-ready-{receipt}=1` at response start. This script-readable SameSite=Strict marker expires after 120 seconds and is Secure on HTTPS. It contains no authorization or file metadata; errors never set it. The UI uses it to indicate download start, not completion. Single-file share metadata contains one non-directory item in `files`, allowing the standalone card without a listing request. Folder shares retain the file browser even with one child.

For a single-file share, the metadata request optionally issues HEAD to the fixed public DSM download route to obtain Content-Length, without reading the file body. Only a successful attachment response supplies `size`. Unsupported HEAD responses, missing lengths and probe failures leave `size` null; the probe is limited to three seconds or the configured metadata timeout, whichever is shorter. Transfer authorization and size limits are still enforced separately when downloading.
