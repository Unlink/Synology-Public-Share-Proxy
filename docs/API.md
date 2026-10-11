# Public API

Only these routes are exposed. Other routes return 404, including DSM paths.

| Method | Route | Request / response |
| --- | --- | --- |
| GET | `/healthz` | `{status:"ok"}`; local process health, no DSM probe |
| GET | `/`, `/sharing/{token}`, `/s/{token}` | Own static UI |
| GET | `/app.css`, `/app.js`, `/icon.svg` | Own assets |
| GET | `/api/session` | HttpOnly visitor cookie and `{csrf,demo}` |
| GET | `/api/shares/{token}` | `{title,description,mode,expiresAt,passwordProtected,maxUploadBytes,files}` |
| POST | `/api/shares/{token}/unlock` | JSON `{password}`; `X-Share-CSRF`; 204 after DSM authorizes the share |
| GET | `/api/shares/{token}/files?folder={id}&offset=0` | `{files,offset,total}`; optional folder ID, 200-item pages |
| POST | `/api/shares/{token}/download` | URL-encoded `csrf` and `ids` (JSON array); native attachment stream |
| POST | `/api/shares/{token}/upload` | Raw `application/octet-stream`, Content-Length, `X-Share-CSRF`, `X-File-Name`, `X-Uploader-Name` |

Filename and uploader headers are `encodeURIComponent` strings, decoded once on the server. No arbitrary URL, host, API, SID, path, Cookie or credentials are accepted. POST Origin must equal the configured public origin. Host is checked except the fixed health endpoint. The visitor cookie is bound to one in-memory session, with independent upstream connections per share. CSRF tokens from another session are rejected.

File DTO: `{id,name,isDirectory,size,modified}`. `size` is bytes or null; `modified` is Unix seconds or null. `id` is random and session-local; it is not a DSM path. A restart invalidates IDs/cookies. Error body: `{code}` without upstream details or secrets. 401 requests a password; 403 rejects an unsupported/forbidden operation; 410 means unavailable; 413 enforces transfer size; 429 asks the visitor to wait; 502/504 cover upstream/protocol failure. Some expired states cannot be distinguished from removed links and intentionally share the unavailable message.

Downloads use a native form/iframe so large binaries do not accumulate in the browser heap. Errors before streaming are JSON and shown by the UI from the same-origin iframe. Errors after streaming starts abort the response; the browser's download manager reports the interruption. Upload progress means browser-to-proxy bytes; at 100% the UI waits for DSM's success response before displaying completion.
