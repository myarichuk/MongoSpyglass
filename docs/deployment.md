# Deployment and TLS

How to run Spyglass on a developer machine and on staging hosts inside a corporate network whose MongoDB
certificates come from an internal CA. Every key mentioned here is defined in
[spec/01-proxy.md §2](spec/01-proxy.md) (proxy) and [spec/03-collector.md §9](spec/03-collector.md) (collector);
copyable files are in [`deploy/examples/`](../deploy/examples).

Spyglass has three network hops, and each one is configured on its own:

```
 app ──(1)──▶ spyglass-proxy ──(2)──▶ MongoDB
                    │
                   (3)
                    ▼
            spyglass-collector ◀── browser (UI)
```

| Hop | Default | TLS turned on by | Trust configured by |
|---|---|---|---|
| (1) app → proxy | plaintext | `listen.tls.mode: tls` + `listen.tls.certFile` | the app (OS store or `tlsCAFile=`) |
| (2) proxy → MongoDB | follows the URI (`tls=true`, `mongodb+srv://`) | `upstream.tls.enabled: true` or the URI | `upstream.tls.caFile`, URI `tlsCAFile=`, or the system store |
| (3) proxy → collector, browser → collector | plain HTTP | an `https://` `collector.endpoint` + `SPYGLASS_TLS_CERT` on the collector (or a reverse proxy) | `collector.caFile` on the proxy, the OS store in the browser |

The UI shows the effective state of all three for every proxy (Topology & producers page): which hops use TLS,
what is trusted, every certificate's subject, issuer, SANs, fingerprint and expiry, and the certificate each
MongoDB member presented. The UI only displays this; certificates and keys are configured on the hosts, never
uploaded through it.

## 1. Developer machine

Nothing to configure for TLS: everything is on loopback.

**Whole stack in Docker** (MongoDB, proxy, collector, a demo app):

```sh
SPYGLASS_TOKEN=dev-token SPYGLASS_ADMIN_PASSWORD=dev-password docker compose up --build
mongosh "mongodb://localhost:27018/?appName=me"      # through the proxy
```

UI at http://localhost:8080.

**Proxy in front of your own local mongod**, using [`deploy/examples/proxy.local.yaml`](../deploy/examples/proxy.local.yaml)
(upstream `localhost:27017`, proxy on `27018`, collector from the compose file):

```sh
SPYGLASS_TOKEN=dev-token dotnet run --project src/Spyglass.Proxy -c Release -- --config deploy/examples/proxy.local.yaml
```

Then change your app's connection string from `localhost:27017` to `localhost:27018` and add `appName=<service>`.

**Your dev database is a TLS staging cluster** (you connect from a laptop to the shared staging MongoDB): the
proxy accepts the same TLS options in the URI your driver uses, so start from the connection string you have:

```sh
spyglass-proxy --upstream.uri "mongodb://mongo-a.staging.corp.example:27017/?replicaSet=rs0&tls=true&tlsCAFile=/path/corp-ca.pem" \
  --listen.basePort 27018 --collector.endpoint http://localhost:8080/ingest/v0 --collector.token dev-token
```

Hop (1) stays plaintext on loopback, so the app connects with `mongodb://localhost:27018/?replicaSet=rs0&appName=me`
(no `tls=true`). On Windows and macOS the corporate root is usually already in the OS store, which the proxy uses
when no `caFile` is given.

## 2. Corporate staging with an internal CA

The typical setup: a TLS replica set with certificates from the company CA, apps on other machines, one staging host
running the proxy (and maybe the collector). Files: [`proxy.staging.yaml`](../deploy/examples/proxy.staging.yaml) and
[`docker-compose.staging.yml`](../deploy/examples/docker-compose.staging.yml).

### 2.1 What to ask your PKI team for

| File | For | Subject / SANs |
|---|---|---|
| `corp-ca.pem` | trusting MongoDB (and the collector) | the root, plus any intermediates, PEM (several certificates in one file is fine) |
| `spyglass-proxy.pem` + `.key` | hop (1): the proxy's server certificate | SAN = the name apps use, the same value as `listen.advertiseHost` |
| `spyglass-collector.pem` | hop (3): the collector's HTTPS certificate | SAN = the collector's name(s) |
| `spyglass-proxy-client.pem` | only if mongod requires client certificates | per your MongoDB TLS policy |

**Certificate formats accepted.** Each of these works for `listen.tls.certFile`, `upstream.tls.certFile` and
`SPYGLASS_TLS_CERT`:

* PEM certificate with the key in a separate `keyFile` (`SPYGLASS_TLS_KEY`);
* one PEM file holding the certificate and the key, like mongod's `tlsCertificateKeyFile`;
* an encrypted PEM key, with `keyPassword` (`SPYGLASS_TLS_KEY_PASSWORD`);
* `.pfx` / `.p12`, which is what Windows CAs usually issue, with an optional password.

Put intermediates in the certificate file after the leaf. They are sent in the handshake, so clients that only
have the root still validate the chain.

### 2.2 Proxy → MongoDB: trust the internal CA

Pick one:

```yaml
upstream:
  uri: "mongodb://mongo-a.staging.corp.example:27017,mongo-b.staging.corp.example:27017/?replicaSet=rs0&tls=true"
  tls:
    caFile: /etc/spyglass/certs/corp-ca.pem
```

* **`upstream.tls.caFile`**, or **`tlsCAFile=` in the URI**: only that bundle is trusted for MongoDB. The chain is
  rebuilt against it with no fallback to the system store, which is the predictable choice for staging. An explicit
  key wins over the URI, and the proxy warns when they disagree.
* **System store.** Leave `caFile` unset. On a Windows or macOS host, and on a Linux host whose image has the corporate
  root installed (`update-ca-certificates`), the proxy trusts what the OS trusts.
* **Linux without root access to the store**: `SSL_CERT_DIR=/etc/ssl/certs:/opt/corp/certs` (a directory hashed
  with `openssl rehash`) or `SSL_CERT_FILE=/opt/corp/ca-bundle.pem`. .NET on Linux reads both through OpenSSL;
  `SSL_CERT_FILE` *replaces* the default bundle rather than adding to it. In containers, use `caFile` instead.

Hostname checking is on: a member's certificate must name the host in the URI or in the replica-set config.
`allowInvalidHostnames: true` turns that off and is shown as a warning in the UI. `allowInvalidCertificates: true`
(or `tlsInsecure=true`) accepts anything, is flagged *insecure* in the UI, and does not belong outside a lab.

`revocation: online | offline` checks CRL or OCSP, but only enable it if the CA's distribution points are
reachable from the proxy host. When they are not, every handshake fails.

**mongod that requires client certificates.** This is `net.tls.mode: requireTLS` with a `CAFile` and without
`allowConnectionsWithoutCertificates`. Give the proxy its own certificate:

```yaml
upstream:
  tls:
    caFile: /etc/spyglass/certs/corp-ca.pem
    certFile: /etc/spyglass/certs/spyglass-proxy-client.pem   # or tlsCertificateKeyFile= in the URI
    keyPassword: "${SPYGLASS_PROXY_CLIENT_KEY_PASSWORD}"
```

This is transport-level only. Apps still authenticate with their own users (SCRAM, LDAP, OIDC), and their
credentials pass through the proxy as before. **MONGODB-X509 and Kerberos (GSSAPI) user authentication do not
work through the proxy** ([01 §7](spec/01-proxy.md)).

### 2.3 App → proxy: serve a corporate certificate

When apps on other machines connect, hop (1) carries their passwords and data, so encrypt it:

```yaml
listen:
  bindAddress: 0.0.0.0
  advertiseHost: spyglass-proxy.staging.corp.example   # must be in the certificate's SANs
  basePort: 27017            # replica set: 27017, 27018, 27019, one per member; open them all
  tls:
    mode: tls
    certFile: /etc/spyglass/certs/spyglass-proxy.pem
    keyFile: /etc/spyglass/certs/spyglass-proxy.key
    keyPassword: "${SPYGLASS_PROXY_KEY_PASSWORD}"
```

Apps change only the host and port and keep `tls=true`. They already trust the corporate root, so nothing else
changes:

```
before: mongodb://mongo-a.staging.corp.example:27017,.../?replicaSet=rs0&tls=true&appName=orders-api
after:  mongodb://spyglass-proxy.staging.corp.example:27017/?replicaSet=rs0&tls=true&appName=orders-api
```

Without a `certFile`, `mode: tls` generates a self-signed certificate at startup and logs its SHA-256 fingerprint
(also shown in `/readyz` and the UI). That is fine for a quick test, but every app then needs that certificate as
its `tlsCAFile`, and it changes on every restart. Use a real certificate on anything shared.

**Passwords.** `keyPassword` values support `${ENV}` and are never logged, exported, or shown in `/readyz`,
`/metrics` or the UI. Prefer the environment over a flag, because flags appear in `ps`. A variable referenced
this way may use the `SPYGLASS_` prefix; it is then treated as a value and not as a config key.

### 2.4 Collector over HTTPS

The UI login and the ingest token travel over hop (3). There are two ways to encrypt it:

* **The collector serves HTTPS itself.** Set the certificate variables:

  ```sh
  SPYGLASS_BIND=0.0.0.0:8443
  SPYGLASS_TLS_CERT=/certs/spyglass-collector.pem   # PEM (cert + key, or with SPYGLASS_TLS_KEY), or .pfx
  SPYGLASS_TLS_KEY_PASSWORD=...                     # if the key or pfx is encrypted
  ```

  The container health check switches to TLS on its own. The session cookie is marked `Secure`.
* **The company reverse proxy or ingress terminates TLS.** Keep the collector on HTTP behind it and set
  `SPYGLASS_TRUSTED_PROXIES` to the reverse proxy's address or CIDR. That way the `X-Forwarded-Proto: https`
  and `X-Forwarded-For` headers it sends are believed. If you use `SPYGLASS_AUTH_HEADER` for single sign-on,
  the same setting applies.

Then point the proxy at it, trusting the corporate CA:

```yaml
collector:
  endpoint: "https://spyglass.staging.corp.example/ingest/v0"
  token: "${SPYGLASS_TOKEN}"
  caFile: /etc/spyglass/certs/corp-ca.pem     # omit if the OS store already trusts it
```

### 2.5 Watch for expiry

The UI marks certificates with fewer than 14 days left, and expired ones, on every hop. The proxy also logs a
startup warning for its own certificates. For alerting, the proxy exports the expiry time of every certificate
it holds or has seen:

```
spyglass_proxy_tls_cert_expiry_seconds{hop="listen|upstream_client|upstream_server",member="..."}   # unix seconds
```

```promql
spyglass_proxy_tls_cert_expiry_seconds - time() < 14 * 86400
```

The collector prints a startup warning for its own certificate. Configuration is read once at startup, so
restart the proxy and collector after renewing certificates.

## 3. Troubleshooting

| Symptom (proxy log) | Cause | Fix |
|---|---|---|
| `certificate not trusted by upstream.tls.caFile: ... PartialChain` / `UntrustedRoot` | the bundle lacks the root that signed MongoDB's certificate, or an intermediate the server does not send | add the root and intermediates to `caFile`; check with `openssl s_client -connect host:27017 -showcerts` |
| `certificate name does not match host` | the URI or replica-set member host is not in the certificate's SANs | use the host names from `rs.conf()`; as a last resort `allowInvalidHostnames: true` |
| `no upstream seed answered` at startup | TLS or network failure to every seed | the lines above it name the failure for each seed |
| app: `certificate verify failed` / `PartialChain` against the proxy | `listen.tls.certFile` lacks the intermediates, or the app does not trust the corporate root | append the intermediates after the leaf; give the app `tlsCAFile=` |
| app: `hostname mismatch` against the proxy | `listen.advertiseHost` is not in the proxy certificate's SANs | reissue the certificate, or change `advertiseHost` |
| app finds the real members and bypasses the proxy | `upstream.uri` has `directConnection=true` against a replica set (hello replies are then not rewritten), or the app's string still lists a real member | drop `directConnection` from the proxy's URI, or connect apps with `directConnection=true` too ([01 §5.1](spec/01-proxy.md)); list only the proxy in the app's string |
| the proxy's own log shows `the private key ... is encrypted; set the matching keyPassword` | an encrypted key without a password | set `keyPassword` (or `SPYGLASS_TLS_KEY_PASSWORD` on the collector) |
| collector: 401 for header auth behind a reverse proxy | the reverse proxy is not in `SPYGLASS_TRUSTED_PROXIES` | add its address or CIDR |

The Topology & producers page answers most of these without a shell: it shows whether TLS is on for each hop,
what is trusted, and every certificate's subject, issuer, SANs and expiry.

## 4. Not supported

* **MONGODB-X509 and GSSAPI (Kerberos) user authentication.** The client's certificate or service ticket is bound
  to the hop it was presented on ([01 §7](spec/01-proxy.md)).
* **`loadBalanced=true` deployments**, which includes Atlas serverless and private-endpoint strings for sharded
  clusters. The proxy refuses them at startup.
* **Reloading configuration or certificates without a restart.**
