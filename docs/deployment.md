# Deployment Guide

## Docker Compose (Production)

The simplest way to run Forgekeeper in production.

### 1. Clone and Configure

```bash
git clone https://github.com/inxaos-repo/forgekeeper.git
cd forgekeeper
cp .env.example .env
```

Edit `.env`:

```env
LIBRARY_PATH=/mnt/nas/3dprinting
FORGEKEEPER_ENCRYPTION_KEY=$(openssl rand -hex 32)
```

### 2. Start

```bash
docker compose up -d
```

This starts:
- **forgekeeper** — the API + frontend on port 5000
- **postgres** — PostgreSQL 16 with pg_trgm extension

### 3. Verify

```bash
curl http://localhost:5000/health
# {"status":"healthy","timestamp":"..."}

# Watch logs
docker compose logs -f forgekeeper
```

### docker-compose.yml Reference

```yaml
services:
  forgekeeper:
    image: ghcr.io/inxaos-repo/forgekeeper:main
    restart: unless-stopped
    ports:
      - "5000:5000"
    environment:
      - ConnectionStrings__ForgeDb=Host=postgres;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper
      - Storage__BasePaths__0=/library
      - FORGEKEEPER_ENCRYPTION_KEY=change-me
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5000
    volumes:
      - ${LIBRARY_PATH:-./sample-library}:/library
      - ./plugins:/app/plugins
      - forgekeeper-data:/data
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:5000/health"]
      interval: 30s
      timeout: 5s
      retries: 3

  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: forgekeeper
      POSTGRES_USER: forgekeeper
      POSTGRES_PASSWORD: forgekeeper
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./init-db.sql:/docker-entrypoint-initdb.d/init.sql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U forgekeeper"]
      interval: 5s
      timeout: 5s
      retries: 5

volumes:
  pgdata:
  forgekeeper-data:
```

### Running E2E Tests

```bash
docker compose run --rm e2e
```

This starts Forgekeeper + PostgreSQL, then runs Playwright tests against the running instance.

---

## Docker Compose (Development)

For active development with hot-reload:

```bash
docker compose -f docker-compose.dev.yml up -d
```

This starts:
- **dev** — .NET 9 SDK container with source mounted (for `dotnet watch run`)
- **db** — PostgreSQL on port 5433 (avoids conflict with production)
- **frontend** — Node.js container running Vite dev server on port 5173

### Backend Hot-Reload

```bash
docker compose -f docker-compose.dev.yml exec dev bash
cd src/Forgekeeper.Api
dotnet watch run
```

### Frontend Hot-Reload

The frontend container runs `npm run dev -- --host 0.0.0.0` automatically on port 5173 with HMR.

---

## Kubernetes with Flux GitOps

For production Kubernetes deployments using Flux CD.

### Directory Structure

The `k8s/` directory contains all manifests:

```
k8s/
├── deployment.yaml     # Forgekeeper Deployment
├── service.yaml        # ClusterIP Service
├── ingress.yaml        # Nginx Ingress with TLS
├── configmap.yaml      # Configuration
└── postgres.yaml       # CNPG Cluster + Secret
```

### Prerequisites

- Kubernetes cluster with:
  - **CNPG operator** (CloudNativePG) for PostgreSQL
  - **nginx-ingress-controller** for Ingress
  - **cert-manager** for TLS certificates
  - **NFS** or equivalent storage for 3D printing files

### Deploy

```bash
# Create namespace
kubectl create namespace forgekeeper

# Apply all manifests
kubectl apply -f k8s/ -n forgekeeper

# Watch rollout
kubectl rollout status deployment/forgekeeper -n forgekeeper
```

### Deployment Manifest

Key configuration in `k8s/deployment.yaml`:

```yaml
spec:
  replicas: 1
  strategy:
    type: Recreate  # Single instance — background workers use file locks
  template:
    spec:
      containers:
        - name: forgekeeper
          image: ghcr.io/inxaos-repo/forgekeeper:main
          imagePullPolicy: Always  # REQUIRED for mutable tags like :main or :latest
          ports:
            - containerPort: 5000
          envFrom:
            - configMapRef:
                name: forgekeeper-config
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
            limits:
              cpu: "2"
              memory: 1Gi
          volumeMounts:
            - name: models
              mountPath: /mnt/3dprinting
            - name: plugins
              mountPath: /app/plugins
              readOnly: true
          livenessProbe:
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 15
            periodSeconds: 30
          readinessProbe:
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 10
      volumes:
        - name: models
          nfs:
            server: 192.168.2.10
            path: /pool/3dprinting
        - name: plugins
          persistentVolumeClaim:
            claimName: forgekeeper-plugins
```

> **Note:** Use `Recreate` strategy (not `RollingUpdate`) because background workers (scanner, thumbnail) use file locks and should not have multiple instances running simultaneously.

---

## NFS Storage Setup

Forgekeeper needs access to your 3D printing file collection, typically over NFS.

### Server Side (Example: Proxmox/ZFS)

```bash
# Install NFS server
apt install nfs-kernel-server

# Export the dataset
echo "/pool/3dprinting *(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
exportfs -a
```

### Kubernetes NFS Volume

```yaml
volumes:
  - name: models
    nfs:
      server: 192.168.2.10      # NFS server IP
      path: /pool/3dprinting     # Export path
```

Or use a PersistentVolume/PersistentVolumeClaim for more flexibility:

```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: forgekeeper-models
spec:
  capacity:
    storage: 4Ti
  accessModes:
    - ReadWriteMany
  nfs:
    server: 192.168.2.10
    path: /pool/3dprinting
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: forgekeeper-models
  namespace: forgekeeper
spec:
  accessModes:
    - ReadWriteMany
  resources:
    requests:
      storage: 4Ti
  volumeName: forgekeeper-models
```

---

## CNPG PostgreSQL

The recommended production database is a [CloudNativePG](https://cloudnative-pg.io/) cluster.

### Cluster Definition

From `k8s/postgres.yaml`:

```yaml
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata:
  name: forgekeeper-db
  namespace: forgekeeper
spec:
  instances: 2  # Primary + 1 replica for HA

  postgresql:
    parameters:
      shared_buffers: "256MB"
      effective_cache_size: "768MB"
      maintenance_work_mem: "64MB"
      max_connections: "100"
    pg_hba:
      - host forgekeeper forgekeeper all md5

  bootstrap:
    initdb:
      database: forgekeeper
      owner: forgekeeper
      secret:
        name: forgekeeper-db-credentials
      postInitSQL:
        - CREATE EXTENSION IF NOT EXISTS pg_trgm;

  storage:
    size: 10Gi
    storageClass: longhorn  # Adjust for your cluster

  monitoring:
    enablePodMonitor: true

  backup:
    barmanObjectStore:
      destinationPath: "s3://backups/forgekeeper/"
      endpointURL: "https://s3.example.com"
      s3Credentials:
        accessKeyId:
          name: forgekeeper-s3-creds
          key: ACCESS_KEY_ID
        secretAccessKey:
          name: forgekeeper-s3-creds
          key: SECRET_ACCESS_KEY
    retentionPolicy: "30d"
```

### Database Credentials

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: forgekeeper-db-credentials
  namespace: forgekeeper
type: kubernetes.io/basic-auth
stringData:
  username: forgekeeper
  password: your-secure-password  # Use sealed-secrets in production
```

### Connection String

CNPG creates a service named `{cluster-name}-rw` for the primary:

```
Host=forgekeeper-db-rw;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=...
```

---

## Ingress Configuration

### Nginx Ingress with TLS

From `k8s/ingress.yaml`:

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: forgekeeper
  namespace: forgekeeper
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
    nginx.ingress.kubernetes.io/proxy-body-size: "100m"
    nginx.ingress.kubernetes.io/proxy-read-timeout: "300"
spec:
  ingressClassName: nginx
  tls:
    - hosts:
        - forge.example.com
      secretName: forgekeeper-tls
  rules:
    - host: forge.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: forgekeeper
                port:
                  name: http
```

Key annotations:
- `proxy-body-size: 100m` — allows large manifest uploads
- `proxy-read-timeout: 300` — long timeout for scan/sync operations

---

## Monitoring

### Health Check Endpoint

`GET /health` returns:

```json
{ "status": "healthy", "timestamp": "2026-04-15T18:00:00Z" }
```

Used by:
- Docker Compose `healthcheck`
- Kubernetes liveness/readiness probes

### Docker Health Check

```yaml
healthcheck:
  test: ["CMD", "wget", "-qO-", "http://localhost:5000/health"]
  interval: 30s
  timeout: 5s
  retries: 3
```

### Kubernetes Probes

```yaml
livenessProbe:
  httpGet:
    path: /health
    port: 5000
  initialDelaySeconds: 15
  periodSeconds: 30
  timeoutSeconds: 5
readinessProbe:
  httpGet:
    path: /health
    port: 5000
  initialDelaySeconds: 5
  periodSeconds: 10
  timeoutSeconds: 3
```

### CNPG Monitoring

With `enablePodMonitor: true`, CNPG exports PostgreSQL metrics for Prometheus. Create a Grafana dashboard or use the CNPG community dashboard.

### Application Logs

Forgekeeper uses Serilog with structured console logging. In Kubernetes, logs are available via:

```bash
kubectl logs -f deployment/forgekeeper -n forgekeeper
```

Adjust log levels via environment variables:

```yaml
Serilog__MinimumLevel__Default: "Information"
Serilog__MinimumLevel__Override__Microsoft: "Warning"
```

### Prometheus Metrics

Forgekeeper exports Prometheus metrics at `GET /metrics`. Add a `ServiceMonitor` to have kube-prometheus-stack scrape it automatically:

```yaml
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: forgekeeper
  namespace: forgekeeper
spec:
  selector:
    matchLabels:
      app: forgekeeper
  endpoints:
    - port: http
      path: /metrics
      interval: 60s
```

### Flux GitOps Deployment

Forgekeeper is deployed via Flux CD using a HelmRelease in the `inxaos-flux` repository. To update:

1. Push a new image tag to GHCR via GitHub Actions CI
2. Flux detects the new image (or update the HelmRelease values)
3. Flux applies the change — Recreate strategy ensures clean rollover

> **Note:** Because the deployment strategy is `Recreate`, there is a brief downtime during rollouts. This is intentional — multiple instances cannot safely share the scanner and thumbnail background workers.

## FlareSolverr (Required for MMF Plugin)

The MyMiniFactory scraper plugin requires FlareSolverr to bypass Cloudflare protection on myminifactory.com.

### Docker Compose

FlareSolverr is included in `docker-compose.yml` and starts automatically:

```yaml
flaresolverr:
  image: ghcr.io/flaresolverr/flaresolverr:latest
  restart: unless-stopped
  ports:
    - "8191:8191"
```

The MMF plugin auto-detects FlareSolverr at `http://flaresolverr:8191` (Docker networking).

### Kubernetes

Deploy FlareSolverr as a separate service. The default plugin config points to:
```
http://flaresolverr.flaresolverr.svc.cluster.local:8191
```

Configure via the Plugins UI: `FLARESOLVERR_URL` field.

### How It Works

1. FlareSolverr creates a browser session and solves the Cloudflare challenge
2. The plugin extracts a CSRF token from the login page
3. Credentials are POSTed to `/login_check` via FlareSolverr
4. Session cookies (REMEMBERME, PHPSESSID, cf_clearance) are extracted
5. A plain HttpClient uses those cookies to fetch the data-library API
6. No Playwright or browser automation needed in the Forgekeeper container
