# Tauri + React + .NET (backend)

## Режимы сети (UDP discovery)

- **Хост** — шлёт UDP beacon (`app`, `role: host`, `tcp` = порт LAN-HTTP) в broadcast на порт из `Net:UdpPort` (`appsettings.json`).
- **Клиент** — слушает тот же UDP-порт; если за `Net:DiscoveryTimeoutMs` пришёл валидный beacon — `state: clientConnected` и `remoteHostBaseUrl`; иначе **graceful degradation** — `state: clientLocalOnly`.

HTTP API backend: `127.0.0.1:<случайный порт>` (задаёт Tauri) и **`http://0.0.0.0:<Net:LanPort>`** для доступа с других ПК в LAN.

### Запуск только из консоли (без Tauri)

Из корня репозитория:

```bash
cd src-dotnet
dotnet run -- --urls http://127.0.0.1:5555
```

Дальше API на `http://127.0.0.1:5555` (например `POST /api/net/start` с `{"mode":"host"}`).

### Проверка на одной машине

- **UDP-порт 49152 занимает только клиент** (слушатель). Хост не биндит этот порт, только шлёт пакеты.
- Два процесса **одновременно** в режиме **клиент** на одном ПК — нельзя (один биндинг на порт).
- **Хост + клиент** на одном ПК — можно: хост шлёт beacon в broadcast и на **127.0.0.1** (чтобы пакет дошёл до локального клиента на той же машине).
- Два **хоста** на одном ПК технически не конфликтуют по UDP, но смысла в обычном тесте нет.

### Проверка на двух машинах

1. Один ПК: в UI нажать **«Хост»** — в статусе `state: hostBeaconing`.
2. Второй ПК в той же подсети: **«Клиент»** — через несколько секунд ожидается `state: clientConnected`, в поле `remoteHostBaseUrl` — URL хоста.
3. При необходимости откройте в брандмауэре **UDP** и **TCP** для портов из `appsettings.json` (`UdpPort`, `LanPort`).

### API

| Метод | Путь | Описание |
|--------|------|----------|
| GET | `/api/net/status` | Поле `state`: `idle` \| `hostBeaconing` \| `clientDiscovering` \| `clientConnected` \| `clientLocalOnly` |
| POST | `/api/net/start` | Тело `{ "mode": "host" \| "client" }` |
| POST | `/api/net/stop` | Остановить beacon / поиск |

## Запуск разработки

```bash
npm install
npm run tauri dev
```

## IDE

- [VS Code](https://code.visualstudio.com/) + [Tauri](https://marketplace.visualstudio.com/items?itemName=tauri-apps.tauri-vscode) + [rust-analyzer](https://marketplace.visualstudio.com/items?itemName=rust-lang.rust-analyzer)
