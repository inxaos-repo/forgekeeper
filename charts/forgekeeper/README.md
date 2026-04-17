# Forgekeeper Helm Chart

A self-hosted 3D print file manager — **Plex for STL files**.

Manage, search, and browse your 3D printing library with thumbnail previews, full-text search, and a plugin system.

## Prerequisites

- Kubernetes 1.24+
- Helm 3.x
- [CloudNativePG (CNPG)](https://cloudnative-pg.io/) operator installed (if using built-in PostgreSQL)
- An ingress controller (e.g., nginx) if enabling ingress

## Installing

```bash
# Add any values overrides to values-override.yaml, then:
helm install forgekeeper ./charts/forgekeeper -n forgekeeper --create-namespace

# Or with inline overrides:
helm install forgekeeper ./charts/forgekeeper -n forgekeeper --create-namespace \
  --set ingress.enabled=true \
  --set ingress.hosts[0].host=forgekeeper.example.com
```

## Configuration

| Parameter | Description | Default |
|-----------|-------------|---------|
| `replicaCount` | Number of replicas | `1` |
| `image.repository` | Container image | `ghcr.io/inxaos-repo/forgekeeper` |
| `image.tag` | Image tag | `main` |
| `image.pullPolicy` | Pull policy | `Always` |
| `service.type` | Service type | `ClusterIP` |
| `service.port` | Service port | `5000` |
| `ingress.enabled` | Enable ingress | `false` |
| `ingress.className` | Ingress class | `nginx` |
| `ingress.hosts` | Ingress host config | see values.yaml |
| `ingress.tls` | TLS config | `[]` |
| `resources.requests.memory` | Memory request | `256Mi` |
| `resources.requests.cpu` | CPU request | `100m` |
| `resources.limits.memory` | Memory limit | `1Gi` |
| `persistence.library.enabled` | Enable library volume | `true` |
| `persistence.library.nfs.enabled` | Use NFS for library | `false` |
| `persistence.library.nfs.server` | NFS server IP | `""` |
| `persistence.library.nfs.path` | NFS export path | `""` |
| `persistence.library.existingClaim` | Use existing PVC | `""` |
| `persistence.library.size` | PVC size | `50Gi` |
| `persistence.data.enabled` | Enable data volume | `true` |
| `persistence.data.size` | Data PVC size | `10Gi` |
| `persistence.plugins.enabled` | Enable plugins volume | `true` |
| `persistence.plugins.size` | Plugins PVC size | `1Gi` |
| `postgresql.enabled` | Deploy CNPG cluster | `true` |
| `postgresql.instances` | PG replicas | `1` |
| `postgresql.storage.size` | PG storage size | `5Gi` |
| `postgresql.external.enabled` | Use external database | `false` |
| `postgresql.external.host` | External DB host | `""` |
| `config.encryptionKey` | Encryption key | `change-me` |
| `config.thumbnails.renderer` | Thumbnail renderer | `stl-thumb` |
| `config.search.minTrigramSimilarity` | Search sensitivity | `0.3` |
| `config.security.apiKey` | API key (empty = no auth) | `""` |
| `autoscaling.enabled` | Enable HPA | `false` |
| `nodeSelector` | Node selector | `{}` |
| `tolerations` | Tolerations | `[]` |
| `affinity` | Affinity rules | `{}` |

## Examples

### Minimal (auto-provisioned storage)

```yaml
# values-minimal.yaml
ingress:
  enabled: true
  hosts:
    - host: forgekeeper.example.com
      paths:
        - path: /
          pathType: Prefix
```

### NFS Library

```yaml
# values-nfs.yaml
persistence:
  library:
    nfs:
      enabled: true
      server: 192.168.4.10
      path: "/warehousepool/3d printing"
    size: 4Ti
  data:
    nfs:
      enabled: true
      server: 192.168.4.10
      path: "/storagepool/docker/forgekeeper"

nodeSelector:
  nodetype: storage

ingress:
  enabled: true
  className: nginx
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-production
  hosts:
    - host: forgekeeper.k8s.inxaos.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - hosts:
        - forgekeeper.k8s.inxaos.com
      secretName: forgekeeper-tls
```

### Full (all options)

```yaml
# values-full.yaml
replicaCount: 1

image:
  repository: ghcr.io/inxaos-repo/forgekeeper
  tag: v0.1.0
  pullPolicy: IfNotPresent

imagePullSecrets:
  - name: ghcr-pull-secret

config:
  encryptionKey: "my-secure-key"
  security:
    apiKey: "my-api-key"

persistence:
  library:
    nfs:
      enabled: true
      server: 192.168.4.10
      path: "/warehousepool/3d printing"
    size: 4Ti
  data:
    size: 10Gi
    storageClass: local-path

postgresql:
  enabled: true
  instances: 2
  storage:
    size: 10Gi
    storageClass: local-path

ingress:
  enabled: true
  className: nginx
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-production
    external-dns.alpha.kubernetes.io/hostname: forgekeeper.k8s.inxaos.com
  hosts:
    - host: forgekeeper.k8s.inxaos.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - hosts:
        - forgekeeper.k8s.inxaos.com
      secretName: forgekeeper-tls

nodeSelector:
  nodetype: storage

resources:
  requests:
    memory: 512Mi
    cpu: 250m
  limits:
    memory: 2Gi
```

## Uninstalling

```bash
helm uninstall forgekeeper -n forgekeeper

# If you want to clean up PVCs too:
kubectl delete pvc -n forgekeeper -l app.kubernetes.io/instance=forgekeeper

# If CNPG cluster was used:
kubectl delete cluster -n forgekeeper -l app.kubernetes.io/instance=forgekeeper
```

> **Note:** NFS PersistentVolumes are created with `Retain` reclaim policy and won't be deleted automatically.
