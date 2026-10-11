Optional: place only your public internal root CA certificate (PEM encoded, .crt extension) here before building.
Dockerfile installs it into the container's system trust store.
Do not put private keys, client certificates or DSM credentials here.
Certificate files are git-ignored. Rebuild the image after replacing a CA.
Use a DSM hostname matching the certificate SAN. TLS validation is always enabled.
