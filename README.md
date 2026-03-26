# OP-1-26 Non-standard Situations

Tauri + React UI и .NET backend с LAN-discovery (UDP) и HTTP-проксированием на найденный host.

## Что внутри

- `src` — UI на React (Tauri frontend)
- `src-tauri` — Tauri (Rust), запускает backend-процесс и отдаёт в UI `baseUrl`
- `src-dotnet` — ASP.NET Core backend
- `DistributedLocalSystem` — библиотека `DistributedLocalSystem.Core` (discovery, UDP, middleware proxy)

## Сетевой режим

Конфигурация задаётся в `src-dotnet/appsettings.json` (`Net`):

- `Role`: `none | host | client`
- `AppId`: идентификатор сервиса в UDP
- `UdpPort`: порт discovery
- `LanPort`: HTTP-порт для доступа по LAN
- `DiscoveryTimeoutMs`: таймаут поиска host

### Поведение ролей

- `host`: шлёт UDP beacon в LAN.
- `client`: ищет host по UDP и при успехе проксирует HTTP-запросы на него.
- `none`: discovery не запускается.

### Ограничение: только один host в LAN

При старте в режиме `host` выполняется preflight-проверка discovery. Если host с тем же `AppId` уже найден в локальной сети, выбрасывается исключение и backend завершается с ошибкой запуска.

### Проксирование в клиентском режиме

- Таймаут запроса к удалённому host: **5 секунд**.
- Если запрос не удался, выполняется проверка `GET /health` удалённого host.
- Если `health` недоступен, текущий host считается потерянным и запускается повторный UDP discovery.

## HTTP API

Основные endpoint'ы backend:

- `GET /health` — healthcheck
- `GET /greet?name=...` — тестовый endpoint
- `GET /api/net/role` — роль из конфигурации (`none | host | client`)
- `GET /api/net/status` — текущее состояние discovery/подключения
- `GET /api/Books` — тестовая коллекция книг (hardcoded)

## Запуск

## 1) Запуск backend из консоли

Из корня репозитория:

```bash
cd src-dotnet
dotnet run -- --urls http://127.0.0.1:5555
```

Backend также откроет LAN-адрес `http://0.0.0.0:<Net:LanPort>`.

## 2) Запуск Tauri приложения

Установить зависимости:

```bash
npm install
```

Запуск dev-режима Tauri:

```bash
npm run tauri dev
```

Важно: `src-tauri` ожидает переменную окружения `BACKEND_EXECUTABLE` (путь к собранному .NET backend executable), иначе backend-процесс не будет запущен из Tauri.

## UI

В интерфейсе доступны:

- отображение роли и статуса discovery (`/api/net/role`, `/api/net/status`)
- кнопка **"Получить книги"** (запрос `GET /api/Books`)

## Технологии

- Tauri v2
- React + TypeScript + Vite
- ASP.NET Core (.NET 8)
- UDP discovery (`UdpDiscovery.Net`)
