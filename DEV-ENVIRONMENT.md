# Forgekeeper Development Environment

## Shared Dev Setup: Visual Studio (Windows) + Docker SDK (your-server)

**How it works:** the developer edits code in Visual Studio on your-desktop (Windows, YOUR_DESKTOP_IP) via SMB share to your-server (YOUR_SERVER_IP). the AI assistant runs builds, tests, and hot-reload inside a .NET 9 SDK Docker container on your-server that volume-mounts the same source directory. Both work on the same files simultaneously — no git needed for the inner dev loop.

```
┌─────────────────────┐         SMB Share          ┌──────────────────────────┐
│  your-desktop (Windows)   │ ◄──────────────────────── │  your-server (Linux)            │
│  YOUR_DESKTOP_IP        │                            │  YOUR_SERVER_IP             │
│                      │                            │                           │
│  Visual Studio 2022  │    \\YOUR_SERVER_IP\share │  Docker containers:       │
│  - Edit .cs files    │    \workspace\projects\    │  - forgekeeper-dev (.NET) │
│  - IntelliSense      │    forgekeeper             │  - forgekeeper-db (PG16)  │
│  - NuGet management  │                            │  - forgekeeper-frontend   │
│  - Remote debug      │                            │                           │
│                      │                            │  the AI assistant (AI):                │
│                      │                            │  - docker exec builds     │
│                      │                            │  - dotnet watch           │
│                      │                            │  - EF migrations          │
└─────────────────────┘                             └──────────────────────────┘
```

---

## 1. Prerequisites

| Component | Required | Notes |
|-----------|----------|-------|
| Docker + Compose | ✅ | Already on your-server |
| SMB share | ✅ | `\\YOUR_SERVER_IP\share` already configured |
| .NET 9 SDK image | Auto-pulled | `mcr.microsoft.com/dotnet/sdk:9.0` |
| PostgreSQL 16 | Auto-pulled | `postgres:16-alpine` |
| Node.js 22 | Auto-pulled | `node:22-alpine` (for Vue.js frontend) |
| Visual Studio 2022+ | On your-desktop | Any edition with .NET 9 SDK workload |

No installation needed on your-desktop beyond Visual Studio — all build tooling lives in containers on your-server.

---

## 2. docker-compose.dev.yml

Create this file at the project root:

```yaml
# /home/openclaw/.openclaw/workspace/projects/forgekeeper/docker-compose.dev.yml
#
# Dev environment: SDK container + Postgres + Node frontend
# Usage: docker compose -f docker-compose.dev.yml up -d

services:
  dev:
    container_name: forgekeeper-dev
    image: mcr.microsoft.com/dotnet/sdk:9.0
    working_dir: /src
    command: sleep infinity
    volumes:
      - /home/openclaw/.openclaw/workspace/projects/forgekeeper:/src
      - nuget-cache:/root/.nuget/packages
    ports:
      - "5000:5000"     # API (Kestrel)
      - "5001:5001"     # API HTTPS (optional)
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_URLS: "http://+:5000"
      ConnectionStrings__ForgeDb: "Host=db;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper"
      DOTNET_USE_POLLING_FILE_WATCHER: "true"  # Required for SMB/network mounts
      Storage__BasePaths__0: /mnt/3dprinting
    depends_on:
      db:
        condition: service_healthy
    networks:
      - forgekeeper-net

  db:
    container_name: forgekeeper-db
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: forgekeeper
      POSTGRES_USER: forgekeeper
      POSTGRES_PASSWORD: forgekeeper
    ports:
      - "5433:5432"     # Accessible from your-desktop at your-server:5433
    volumes:
      - pgdata-dev:/var/lib/postgresql/data
      - ./init-db.sql:/docker-entrypoint-initdb.d/init.sql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U forgekeeper"]
      interval: 5s
      timeout: 5s
      retries: 5
    networks:
      - forgekeeper-net

  frontend:
    container_name: forgekeeper-frontend
    image: node:22-alpine
    working_dir: /app
    command: sleep infinity
    volumes:
      - /home/openclaw/.openclaw/workspace/projects/forgekeeper/src/Forgekeeper.Frontend:/app
      - node-modules:/app/node_modules
    ports:
      - "5173:5173"     # Vite dev server
    networks:
      - forgekeeper-net

volumes:
  pgdata-dev:
  nuget-cache:          # Persists NuGet packages across container recreations
  node-modules:         # Persists node_modules across container recreations

networks:
  forgekeeper-net:
```

### Key design decisions

- **`sleep infinity`** — Containers stay alive for `docker exec` workflow. No auto-build on startup.
- **`DOTNET_USE_POLLING_FILE_WATCHER: true`** — Critical. Without this, `dotnet watch` won't detect file changes over SMB because inotify doesn't work on network mounts.
- **`nuget-cache` named volume** — NuGet packages survive `docker compose down` / `up` cycles. First restore downloads everything; subsequent builds are instant.
- **Postgres on port 5433** — Avoids conflicts with any local Postgres. Accessible from your-desktop at `YOUR_SERVER_IP:5433`.
- **Separate network** — Containers reference each other by service name (`db`, `dev`, `frontend`).

---

## 3. First-Time Setup

```bash
cd /home/openclaw/.openclaw/workspace/projects/forgekeeper

# Start all containers
docker compose -f docker-compose.dev.yml up -d

# Wait for postgres health check
docker compose -f docker-compose.dev.yml ps  # db should show "healthy"

# Initial NuGet restore (downloads all packages — takes a minute)
docker exec forgekeeper-dev dotnet restore

# Verify build
docker exec forgekeeper-dev dotnet build --no-restore

# Apply EF migrations (if any exist)
docker exec forgekeeper-dev dotnet ef database update \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api
```

### NuGet Authentication (if using private feeds)

If the project uses authenticated NuGet sources, create a `nuget.config` at the project root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

For private feeds, add credentials inside the container:
```bash
docker exec forgekeeper-dev dotnet nuget add source "https://feed.example.com/v3/index.json" \
  --name PrivateFeed --username USER --password TOKEN --store-password-in-clear-text
```

---

## 4. the developer's Workflow (Visual Studio on your-desktop)

### Opening the project

1. Open Visual Studio 2022
2. File → Open → Project/Solution
3. Navigate to `\\YOUR_SERVER_IP\share\workspace\projects\forgekeeper\Forgekeeper.sln`
4. VS will load the solution with full IntelliSense (it reads .csproj files directly)

### Daily workflow

- **Edit code** — Save triggers hot reload if `dotnet watch` is running in the container
- **Add NuGet packages** — Use VS NuGet Package Manager. Run `dotnet restore` inside the container afterward (or ask the AI assistant to do it)
- **View API** — Browse to `http://YOUR_SERVER_IP:5000/swagger` (or whatever endpoint is configured)
- **View database** — Connect any SQL client to `YOUR_SERVER_IP:5433` with `forgekeeper/forgekeeper`
- **Frontend** — Browse to `http://YOUR_SERVER_IP:5173` when the Vite dev server is running

### IntelliSense notes

Visual Studio IntelliSense works from the .csproj and source files directly — it doesn't need the container running. NuGet packages are resolved by VS's own SDK. The container has its own NuGet cache (the named volume), so packages need to be restored in both places:
- VS restores automatically when you open the solution (populates its local cache)
- The container needs an explicit `dotnet restore` (populates the named volume)

---

## 5. AI Assistant Workflow (Docker Exec on your-server)

All commands run through `docker exec` into the SDK container:

```bash
# Build the solution
docker exec forgekeeper-dev dotnet build

# Run tests
docker exec forgekeeper-dev dotnet test

# Run the API server
docker exec forgekeeper-dev dotnet run --project src/Forgekeeper.Api

# Run with hot reload (watches for file changes)
docker exec forgekeeper-dev dotnet watch run --project src/Forgekeeper.Api

# Run a specific test
docker exec forgekeeper-dev dotnet test --filter "FullyQualifiedName~MyTestName"

# Check for build errors without building binaries
docker exec forgekeeper-dev dotnet build --no-incremental 2>&1 | head -50
```

### Interactive vs detached

For long-running processes like `dotnet watch`, run in the background:

```bash
# Background the watch process
docker exec -d forgekeeper-dev dotnet watch run --project src/Forgekeeper.Api

# Check logs
docker logs forgekeeper-dev

# Or use exec with nohup for better control
docker exec forgekeeper-dev bash -c 'nohup dotnet watch run --project src/Forgekeeper.Api > /tmp/watch.log 2>&1 &'
docker exec forgekeeper-dev cat /tmp/watch.log
```

### Coordination with the developer

- **Before building:** Let the developer know if he has unsaved changes (a build mid-save can pick up partial files)
- **Before editing:** Don't modify files the developer is actively editing — ask or check timestamps
- **After NuGet changes:** If the developer adds a package via VS, run `dotnet restore` in the container
- **After migrations:** Tell the developer the connection string hasn't changed, but schema may have

---

## 6. Hot Reload

Hot reload uses `dotnet watch`, which monitors the filesystem for changes and automatically rebuilds/restarts.

```bash
# Start hot reload
docker exec -it forgekeeper-dev dotnet watch run --project src/Forgekeeper.Api
```

### How it works with SMB

1. the developer saves a file in VS → write goes over SMB to your-server filesystem
2. `DOTNET_USE_POLLING_FILE_WATCHER=true` makes dotnet watch poll the filesystem (every ~2 seconds)
3. Change detected → rebuild → restart Kestrel
4. Both the developer and the AI assistant's edits trigger the same reload cycle

### What hot reload handles

| Change Type | Hot Reload? | Notes |
|------------|-------------|-------|
| Method body changes | ✅ Yes | In-process reload, no restart |
| New methods/classes | ✅ Yes | Rebuild + restart |
| .csproj changes | ❌ No | Restart `dotnet watch` |
| New NuGet packages | ❌ No | Run `dotnet restore` first |
| appsettings.json | ✅ Yes | If using reloadOnChange (default) |

### Gotcha: Polling interval

With `DOTNET_USE_POLLING_FILE_WATCHER`, there's a ~2-4 second delay between save and detection. This is normal for network mounts.

---

## 7. NuGet Package Management

### How the caching works

```
┌───────────────────┐     ┌─────────────────────┐     ┌──────────────┐
│  the developer (VS)       │     │  Container           │     │  nuget.org   │
│  Local NuGet cache│     │  /root/.nuget/pkgs   │     │              │
│  (Windows)        │     │  (named volume)      │     │              │
└───────────────────┘     └─────────────────────┘     └──────────────┘
        │                           │                         │
        │  dotnet restore           │  dotnet restore         │
        │  (VS auto-restore)        │  (docker exec)          │
        ▼                           ▼                         │
   Populates Windows          Populates named volume          │
   NuGet cache                (persists across                │
                               container recreations)         │
```

- **First restore** in the container downloads everything from nuget.org (~30-60 seconds depending on package count)
- **Subsequent restores** are instant — packages come from the `nuget-cache` named volume
- **Adding a package:** the developer adds via VS NuGet Manager → the AI assistant runs `docker exec forgekeeper-dev dotnet restore`
- **The two caches are independent** — VS has its own on Windows, the container has its own in the named volume

### Force a clean restore

```bash
docker exec forgekeeper-dev dotnet nuget locals all --clear
docker exec forgekeeper-dev dotnet restore
```

---

## 8. Database Management

### Connection strings

| From | Connection String |
|------|------------------|
| Inside container | `Host=db;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper` |
| From your-desktop (VS/tools) | `Host=YOUR_SERVER_IP;Port=5433;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper` |
| From your-server host | `Host=localhost;Port=5433;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper` |

### EF Core Migrations

```bash
# Apply existing migrations
docker exec forgekeeper-dev dotnet ef database update \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api

# Create a new migration
docker exec forgekeeper-dev dotnet ef migrations add MigrationName \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api

# Rollback last migration
docker exec forgekeeper-dev dotnet ef database update PreviousMigrationName \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api

# Generate SQL script (for review)
docker exec forgekeeper-dev dotnet ef migrations script \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api
```

### Database reset

```bash
# Drop and recreate
docker exec forgekeeper-db psql -U forgekeeper -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
docker exec forgekeeper-db psql -U forgekeeper -f /docker-entrypoint-initdb.d/init.sql

# Then re-apply migrations
docker exec forgekeeper-dev dotnet ef database update \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api
```

### Direct SQL access

```bash
# Interactive psql
docker exec -it forgekeeper-db psql -U forgekeeper

# Run a query
docker exec forgekeeper-db psql -U forgekeeper -c "SELECT count(*) FROM models;"
```

---

## 9. Debugging

### Remote debugging from Visual Studio

1. Start the API in the container with debugging enabled:
   ```bash
   docker exec -d forgekeeper-dev dotnet run --project src/Forgekeeper.Api --configuration Debug
   ```

2. In Visual Studio:
   - Debug → Attach to Process
   - Connection type: **SSH**
   - Connection target: `YOUR_SERVER_IP` (your-server)
   - Or use **Docker** connection type if VS has Docker tools pointing to your-server

3. Alternatively, install `vsdbg` in the container for full debug support:
   ```bash
   docker exec forgekeeper-dev bash -c \
     'curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg'
   ```
   Then attach to the `dotnet` process using the remote debugger path `/vsdbg/vsdbg`.

### Console debugging (AI assistant)

```bash
# Check what's running
docker exec forgekeeper-dev ps aux

# Read application logs
docker exec forgekeeper-dev cat /tmp/watch.log

# Tail logs in real-time
docker exec forgekeeper-dev tail -f /tmp/watch.log

# Check for unhandled exceptions
docker exec forgekeeper-dev dotnet run --project src/Forgekeeper.Api 2>&1 | grep -i "exception\|error\|fail"
```

---

## 10. Vue.js Frontend

The frontend container runs Node.js for the Vue.js/Vite dev server.

### First-time setup

```bash
# Install dependencies
docker exec forgekeeper-frontend npm install

# Start Vite dev server
docker exec forgekeeper-frontend npm run dev -- --host 0.0.0.0
```

### Daily workflow

```bash
# Start dev server (accessible at http://YOUR_SERVER_IP:5173)
docker exec -d forgekeeper-frontend npm run dev -- --host 0.0.0.0

# Install a new package
docker exec forgekeeper-frontend npm install axios

# Run linting
docker exec forgekeeper-frontend npm run lint

# Build for production
docker exec forgekeeper-frontend npm run build
```

### API proxy

Configure `vite.config.ts` to proxy API requests to the .NET container:

```typescript
export default defineConfig({
  server: {
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: 'http://dev:5000',
        changeOrigin: true,
      },
    },
  },
});
```

The `dev` hostname resolves inside the Docker network to the .NET SDK container.

---

## 11. Common Commands Cheat Sheet

### For the developer (Visual Studio)

| Action | How |
|--------|-----|
| Open project | `\\YOUR_SERVER_IP\share\workspace\projects\forgekeeper\Forgekeeper.sln` |
| View API | `http://YOUR_SERVER_IP:5000` |
| View frontend | `http://YOUR_SERVER_IP:5173` |
| Connect to DB | Host: `YOUR_SERVER_IP`, Port: `5433`, User: `forgekeeper`, Pass: `forgekeeper` |
| Add NuGet package | VS NuGet Manager, then tell the AI assistant to `dotnet restore` |

### For the AI assistant (Docker Exec)

| Action | Command |
|--------|---------|
| Start environment | `docker compose -f docker-compose.dev.yml up -d` |
| Stop environment | `docker compose -f docker-compose.dev.yml down` |
| Build | `docker exec forgekeeper-dev dotnet build` |
| Test | `docker exec forgekeeper-dev dotnet test` |
| Watch (hot reload) | `docker exec forgekeeper-dev dotnet watch run --project src/Forgekeeper.Api` |
| Run once | `docker exec forgekeeper-dev dotnet run --project src/Forgekeeper.Api` |
| Restore packages | `docker exec forgekeeper-dev dotnet restore` |
| Add migration | `docker exec forgekeeper-dev dotnet ef migrations add NAME --project src/Forgekeeper.Infrastructure --startup-project src/Forgekeeper.Api` |
| Apply migrations | `docker exec forgekeeper-dev dotnet ef database update --project src/Forgekeeper.Infrastructure --startup-project src/Forgekeeper.Api` |
| Interactive DB | `docker exec -it forgekeeper-db psql -U forgekeeper` |
| Container shell | `docker exec -it forgekeeper-dev bash` |
| Check container status | `docker compose -f docker-compose.dev.yml ps` |
| View build errors | `docker exec forgekeeper-dev dotnet build 2>&1 \| tail -30` |
| Start frontend | `docker exec -d forgekeeper-frontend npm run dev -- --host 0.0.0.0` |
| Frontend install | `docker exec forgekeeper-frontend npm install` |

---

## 12. Troubleshooting

### SMB file locking

**Symptom:** Build fails with "file in use" or VS shows "file has been modified outside the editor."

**Fix:** SMB locks can be sticky. If a build process crashes mid-write:
```bash
# Restart the dev container (releases all locks)
docker restart forgekeeper-dev
```

VS may also hold locks on `.suo` and `.user` files — these are in the `.vs/` directory and don't affect builds.

### Permission issues

**Symptom:** `dotnet build` fails with permission denied on `/src/...`

**Fix:** The container runs as root by default, so this usually means the host filesystem permissions are wrong:
```bash
# On your-server, check ownership
ls -la /home/openclaw/.openclaw/workspace/projects/forgekeeper/

# Fix if needed (the SMB share should handle this, but just in case)
sudo chown -R 1000:1000 /home/openclaw/.openclaw/workspace/projects/forgekeeper/
```

### Container won't start

```bash
# Check logs
docker compose -f docker-compose.dev.yml logs dev
docker compose -f docker-compose.dev.yml logs db

# Nuclear option: rebuild everything
docker compose -f docker-compose.dev.yml down -v  # WARNING: -v deletes volumes (NuGet cache + DB data)
docker compose -f docker-compose.dev.yml up -d
docker exec forgekeeper-dev dotnet restore
```

### NuGet restore fails

**Symptom:** `Unable to load the service index for source https://api.nuget.org/v3/index.json`

```bash
# Check DNS inside container
docker exec forgekeeper-dev nslookup api.nuget.org

# Check internet access
docker exec forgekeeper-dev curl -sI https://api.nuget.org/v3/index.json

# If DNS is broken, restart Docker
sudo systemctl restart docker
docker compose -f docker-compose.dev.yml up -d
```

### dotnet watch doesn't detect changes

**Symptom:** You save a file but nothing rebuilds.

**Checklist:**
1. Is `DOTNET_USE_POLLING_FILE_WATCHER=true` set? (Check with `docker exec forgekeeper-dev env | grep POLLING`)
2. Is `dotnet watch` actually running? (`docker exec forgekeeper-dev ps aux | grep watch`)
3. Wait 4-5 seconds — polling has a delay on network mounts
4. Restart watch: kill the existing process and re-run

### Postgres connection refused

```bash
# Check if postgres is healthy
docker compose -f docker-compose.dev.yml ps db

# If not healthy, check logs
docker compose -f docker-compose.dev.yml logs db

# Common fix: volume corruption after hard restart
docker compose -f docker-compose.dev.yml down
docker volume rm forgekeeper_pgdata-dev
docker compose -f docker-compose.dev.yml up -d
# WARNING: This loses all DB data. Re-apply migrations afterward.
```

### Port conflicts

If ports 5000, 5433, or 5173 are already in use:
```bash
# Find what's using the port
sudo ss -tlnp | grep -E '5000|5433|5173'

# Change ports in docker-compose.dev.yml if needed
```

### obj/bin directory conflicts

If the developer runs a build locally in VS and the container also builds, the `obj/` and `bin/` directories can conflict (different SDKs, different RIDs).

**Fix:** Use the container as the single build environment. VS IntelliSense doesn't need a full build — it works from source analysis. If conflicts occur:
```bash
docker exec forgekeeper-dev dotnet clean
docker exec forgekeeper-dev rm -rf src/*/obj src/*/bin tests/*/obj tests/*/bin
docker exec forgekeeper-dev dotnet restore
docker exec forgekeeper-dev dotnet build
```
