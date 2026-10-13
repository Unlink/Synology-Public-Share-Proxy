# Synology Public Share Proxy

Public File Station download links and anonymous File Request uploads through a dedicated interface. Built with ASP.NET Core Minimal API and vanilla JavaScript. DSM manages files, passwords and permissions; the application does not require a DSM account or a mounted file volume.

[API documentation](docs/API.md)

## Docker image

After successful checks, the workflow in `.github/workflows/docker.yaml` publishes images for `linux/amd64` and `linux/arm64`:

```text
ghcr.io/unlink/synology-public-share-proxy:latest
```

A push to the default branch updates `latest` and creates a `sha-<commit>` tag. A `v1.2.3` tag creates versions `1.2.3` and `1.2`. You can also run the workflow manually through GitHub Actions from the default branch. Pull requests run checks only. Publishing uses the automatic `GITHUB_TOKEN`, so a personal access token is not required. To allow image pulls without authentication, set the package visibility to Public in GitHub Packages after the first publication. For a private package, Container Manager must authenticate with GHCR.

Details: [publishing images to GHCR](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images).

## Synology deployment example

Requires a NAS with Container Manager and amd64 or arm64 support. All domains and IP addresses below are placeholders; replace them with your own values.

1. Create the directory `/volume1/docker/synology-public-share-proxy`. Place `compose.yaml` from this repository in it and copy `.env.example` to `.env`.
2. Configure `.env`, for example:

   ```dotenv
   DSM_ORIGIN=https://nas.example.com:5001
   PUBLIC_ORIGIN=https://share.example.com
   PROXY_PORT=8088
   UPLOADS_ENABLED=true
   PASSWORD_LOGIN_ENABLED=true
   MAX_UPLOAD_BYTES=1073741824
   MAX_DOWNLOAD_BYTES=10737418240
   ```

   The DSM address must be reachable from the container, and its HTTPS certificate must match the hostname and be trusted. For a local connection to the NAS, you can use, for example, `DSM_ORIGIN=http://192.168.1.20:5000`. HTTP and HTTPS are supported for any configured DSM hostname or IP address. Localhost in a standard container refers to the container itself. The origin must include http:// or https:// and the DSM port, without a /sharing path, credentials or query. The DSM address must not point back to this proxy. The public `PUBLIC_ORIGIN` accepts HTTP or HTTPS and must match the address used by visitors. Session cookies use Secure and the application sends HSTS when the public origin uses HTTPS; HTTP connections are unencrypted. Keep `.env` out of Git; no DSM username or password is configured.
3. Open **Container Manager → Project → Create**, select the directory you created and its existing `compose.yaml`, then start the project. Compose uses the image from GHCR. The equivalent commands over SSH, run from that directory, are:

   ```sh
   docker compose pull
   docker compose up -d
   ```

4. In **Control Panel → Login Portal → Advanced → Reverse Proxy**, create a rule:

   | Setting | Value |
   | --- | --- |
   | Source | HTTPS, `share.example.com`, port `443` |
   | Destination | HTTP, `127.0.0.1`, port `8088` |

   Assign a certificate to the public domain. Preserve the original Host and Origin. Adjust the reverse proxy request size limits and timeouts to match the upload limit and transfer duration; disable buffering for large files if your configuration supports it. An example for an nginx instance you manage is in `deploy/nginx.example.conf`.
5. Route the public domain only to this container. The container port is bound to loopback; do not expose the DSM administration interface through this domain. Prevent reverse proxy access/error logs from recording URLs containing share tokens. For larger numbers of visitors, configure per-client rate limiting on the reverse proxy, since the application uses the direct connection IP address.
6. Create a link through DSM File Station and change only its host:

   ```text
   DSM:   https://nas.example.com:5001/sharing/EXAMPLE123
   Proxy: https://share.example.com/sharing/EXAMPLE123
   ```

   `EXAMPLE123` is a fictional token. DSM continues to manage expiration and the password. Public shares and File Request are supported; links requiring a DSM account or a visit counter are rejected.

Container Manager and reverse proxy configuration are described in [Synology Project](https://kb.synology.com/en-global/DSM/help/ContainerManager/docker_project) and [Login Portal](https://kb.synology.com/en-af/DSM/help/DSM/AdminCenter/system_login_portal?version=7). Web Station is not required when configuring the reverse proxy directly.

To update to a newly published image:

```sh
docker compose pull
docker compose up -d
```

Compose runs the process as a non-root user, with a read-only filesystem and no NAS file volumes. Diagnostic logs are available in Container Manager or through `docker compose logs --tail=100 share-proxy`; they contain a request identifier and safe status information, without URLs, passwords, cookies or filenames. Set `DIAGNOSTICS_VERBOSE=true` in `.env` to enable more detailed operation status logs.

## Standalone Compose example for Container Manager

Ready-to-use file: [compose.synology.example.yaml](compose.synology.example.yaml).

In **Container Manager → Project → Create**, choose a project directory and paste the following into the Compose editor. Replace the two example origins before starting the project. The image must have been published by the GitHub workflow; use a public GHCR package or configure registry authentication in Container Manager.

```yaml
services:
  share-proxy:
    image: ghcr.io/unlink/synology-public-share-proxy:latest
    restart: unless-stopped
    ports:
      - "127.0.0.1:8088:8080"
    environment:
      Proxy__DsmOrigin: "http://192.168.1.20:5000"
      Proxy__PublicOrigin: "https://share.example.com"
      Proxy__UploadsEnabled: "true"
      Proxy__PasswordLoginEnabled: "true"
      Proxy__MaxUploadBytes: "1073741824"
      Proxy__MaxDownloadBytes: "10737418240"
      Proxy__MaxConcurrentTransfers: "4"
      Proxy__TransferTimeoutSeconds: "1800"
      Proxy__IdleTimeoutSeconds: "60"
      Proxy__DiagnosticsVerbose: "false"
    read_only: true
    tmpfs:
      - /tmp:size=16m,mode=1777
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    mem_limit: 256m
    logging:
      driver: json-file
      options:
        max-size: "5m"
        max-file: "2"
```

This example keeps all settings in Compose and does not require an `.env` file. The DSM origin is a private address reachable from the container. Configure the HTTPS reverse proxy described above with destination `http://127.0.0.1:8088`. Do not publish the container's port on all network interfaces.

CPU and PID cgroup limits are omitted because some Synology kernels do not support them. The memory limit and other container restrictions remain enabled. A `Proxy started` log means the server is running and waiting for requests; the process normally stays active until it is stopped.

To connect to DSM on the same Docker host without a DSM TLS certificate, add the following settings under `share-proxy` in the example above (keep the other environment settings):

```yaml
    extra_hosts:
      - "host.docker.internal:host-gateway"
    environment:
      Proxy__DsmOrigin: "http://host.docker.internal:5000"
```

Docker maps this exact hostname to the host gateway through the container's hosts file. DSM must listen on port 5000 on that interface, and the NAS firewall must allow the container network. This requires Docker's `host-gateway` support; if the engine rejects it, use the NAS private IP directly. After changing environment settings, recreate the container. The public proxy origin can use HTTP or HTTPS independently of this DSM connection. See [Docker's host gateway documentation](https://docs.docker.com/reference/cli/docker/container/run/#add-host).
