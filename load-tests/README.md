# HonestLicenseServer load tests

Набор k6-тестов разделён на безопасные read-only запросы и явно разрешаемые записи в отдельную test environment. Ни один скрипт не содержит паролей, access/refresh tokens или admin key.

## Требования и границы безопасности

- Установить [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/). На Windows: `winget install k6.k6`.
- Всегда задавать `BASE_URL` явно. Финальный `/` не нужен.
- `production-safe.js` выполняет только `GET` и никогда не вызывает login, refresh или logout.
- `non-production-writes.js` прекращает работу до первого HTTP-запроса, если одновременно не заданы `ALLOW_WRITES=true` и `TARGET_ENV=non-production`.
- Не передавать secrets через `-e`, поскольку командная строка может попасть в историю процессов. Использовать переменные текущей PowerShell-сессии и очищать их после теста.

`POST /api/auth/login` создаёт persistent refresh session, а `POST /api/auth/refresh` ротирует её и пишет новую строку в SQLite. Поэтому эти endpoints отсутствуют в production-safe сценарии.

## Классификация актуального API

Read-only endpoints:

| Endpoint | Авторизация | Production-safe load |
| --- | --- | --- |
| `GET /api/version/current/{application}` | нет | да |
| `GET /api/configuration/current` | active-device bearer | да |
| `GET /api/license/current` | active-device bearer | да; поддерживается ETag/304 |
| `GET /api/device/registration/current` | active-client bearer | да |
| `GET /api/assets/{component}/{version}/download` | active-device bearer | нет: может обращаться к внешнему Yandex resolver |
| `GET /api/admin/clients` | admin key | нет: не типичный HonestFlow traffic |
| `GET /api/admin/clients/{clientId}` | admin key | нет |
| `GET /api/admin/devices` | admin key | нет |
| `GET /api/admin/licenses` | admin key | нет |
| `GET /api/admin/licenses/{id}` | admin key | нет |
| `GET /api/admin/versions` | admin key | нет |
| `GET /api/admin/device-requests` | admin key | нет |
| `GET /api/admin/assets` | admin key | нет |
| `GET /api/admin/clients/{clientId}/component-versions` | admin key | нет |
| `GET /api/admin/clients/{clientId}/integration-settings` | admin key | нет |
| `GET /api/admin/support-requests` | admin key | нет |

State-changing endpoints, запрещённые для production load:

- `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout` — создают, ротируют или отзывают sessions;
- `POST /api/device/request` — создаёт/обновляет registration request;
- `POST /api/support/requests` и `POST /api/connection-requests` — создают persistent requests;
- `POST /api/admin/clients`, `PUT /api/admin/clients/{clientId}`;
- `POST /api/admin/devices`, `PUT /api/admin/devices/{id}`;
- `POST /api/admin/licenses`, `PUT /api/admin/licenses/{id}/revoke`;
- `PUT /api/admin/versions/{application}`;
- `PUT /api/admin/device-requests/{id}/approve`, `PUT /api/admin/device-requests/{id}/reject`;
- `PUT /api/admin/assets/{component}/{version}`;
- `PUT /api/admin/clients/{clientId}/integration-settings`;
- `PUT /api/admin/clients/{clientId}/component-versions/{component}`;
- `PUT /api/admin/support-requests/{id}/resolve`.

Текущий OpenAPI не содержит `DELETE` endpoints.

## Production-safe запуск на Windows

Открыть PowerShell в корне репозитория:

```powershell
$env:BASE_URL = 'https://license.example.test'
$env:ACCESS_TOKEN = '<готовый test access token активного устройства>'
$env:APPLICATION = 'HonestFlow'
```

Smoke, 1 → 5 VU:

```powershell
$env:PROFILE = 'smoke'
k6 run .\load-tests\production-safe.js
```

Normal, 25 → 50 → 100 VU:

```powershell
$env:PROFILE = 'normal-load'
k6 run .\load-tests\production-safe.js
```

Stress, 100 → 200 → 300 → 500 VU:

```powershell
$env:PROFILE = 'stress'
k6 run .\load-tests\production-safe.js
```

Spike, 10 → 300 → 10 VU:

```powershell
$env:PROFILE = 'spike'
k6 run .\load-tests\production-safe.js
```

Soak, по умолчанию 50 VU / 2 часа:

```powershell
$env:PROFILE = 'soak'
$env:SOAK_VUS = '50'
$env:SOAK_DURATION = '2h'
k6 run .\load-tests\production-safe.js
```

Access token HonestLicenseServer живёт 15 минут. Скрипт намеренно не обновляет его, потому что refresh меняет server state. Поэтому authenticated production-safe run должен укладываться в срок жизни токена. Для длинного production soak отключить authenticated reads и нагрузить только публичный version endpoint:

```powershell
$env:INCLUDE_AUTH_READS = 'false'
$env:INCLUDE_VERSION_READ = 'true'
$env:PROFILE = 'soak'
k6 run .\load-tests\production-safe.js
```

После запуска удалить token из текущей сессии:

```powershell
Remove-Item Env:\ACCESS_TOKEN -ErrorAction SilentlyContinue
```

## Настройка нагрузки и thresholds

Начальные thresholds:

- `http_req_failed: rate < 0.01`;
- `http_req_duration: p(95) < 500 ms`;
- `http_req_duration: p(99) < 1000 ms`;
- custom `api_error_rate: rate < 0.01`.

Результат выводит `avg`, `p90`, `p95`, `p99` и `max`; request rate доступен как `http_reqs/s`.

Общие переменные:

| Variable | Default | Назначение |
| --- | --- | --- |
| `BASE_URL` | обязательно | target API |
| `PROFILE` | `smoke` | `smoke`, `normal-load`, `stress`, `spike`, `soak` |
| `ACCESS_TOKEN` | обязательно для auth reads | готовый access token |
| `INCLUDE_AUTH_READS` | `true` | configuration/license/registration status |
| `INCLUDE_VERSION_READ` | `true` | public version read |
| `APPLICATION` | `HonestFlow` | version route value |
| `THINK_TIME_SECONDS` | `5` | пауза между итерациями |
| `REQUEST_PAUSE_SECONDS` | `0.25` | пауза между read requests |
| `HTTP_REQ_FAILED_RATE` | `0.01` | максимальная доля ошибок |
| `P95_MS` | `500` | p95 threshold |
| `P99_MS` | `1000` | p99 threshold |

Профили полностью переопределяются env-переменными: `SMOKE_*`, `NORMAL_STAGE_*`, `STRESS_STAGE_*`, `SPIKE_*`, `SOAK_VUS`, `SOAK_DURATION`. Точные имена и defaults находятся в `lib/profiles.js`.

Пример изменения threshold и normal peak:

```powershell
$env:P95_MS = '750'
$env:P99_MS = '1500'
$env:NORMAL_STAGE_3_VUS = '150'
$env:PROFILE = 'normal-load'
k6 run .\load-tests\production-safe.js
```

## Non-production auth/write сценарии

`active-auth` делает один login на VU, выполняет configuration/license reads и периодически refresh. Login не выполняется на каждой итерации.

```powershell
$env:BASE_URL = 'https://license-test.example.test'
$env:TARGET_ENV = 'non-production'
$env:ALLOW_WRITES = 'true'
$env:TEST_PASSWORD = '<test client password>'
$env:DEVICE_ID = '<registered test device id>'
$env:WRITE_FLOW = 'active-auth'
$env:REFRESH_EVERY_ITERATIONS = '10'
$env:PROFILE = 'smoke'
k6 run .\load-tests\non-production-writes.js
```

`unknown-device-registration` создаёт отдельную unknown-device session на VU, отправляет одну registration request на VU и читает её статус:

```powershell
$env:BASE_URL = 'https://license-test.example.test'
$env:TARGET_ENV = 'non-production'
$env:ALLOW_WRITES = 'true'
$env:TEST_PASSWORD = '<test client password>'
$env:DEVICE_ID_PREFIX = 'k6-run-20260811'
$env:REGISTRATION_ADDRESS = 'k6 isolated test address'
$env:WRITE_FLOW = 'unknown-device-registration'
$env:PROFILE = 'smoke'
k6 run .\load-tests\non-production-writes.js
```

Approve/reject и license publication/revoke намеренно не автоматизированы: они требуют admin key и, для publication, корректно подписанный ECDSA grant. Их массовый запуск имеет разрушительную семантику и должен проектироваться только под одноразовую изолированную БД.

## Ubuntu monitoring во время теста

Имя systemd unit в репозитории — `honestserver`.

Статус и uptime процесса:

```bash
sudo systemctl status honestserver --no-pager
pid=$(systemctl show honestserver -p MainPID --value)
ps -p "$pid" -o pid,lstart,etime,%cpu,%mem,rss,vsz,cmd
```

CPU/RAM в реальном времени:

```bash
pid=$(systemctl show honestserver -p MainPID --value)
pidstat -r -u -p "$pid" 1
vmstat 1
```

Если `pidstat` отсутствует: `sudo apt install sysstat` вне окна тестирования.

Логи приложения:

```bash
sudo journalctl -u honestserver -f -o short-iso
sudo journalctl -u honestserver --since "10 minutes ago" -p warning..alert --no-pager
```

HTTP 4xx/5xx нужно считать по access log reverse proxy. Стандартный ASP.NET console log не гарантирует запись каждого HTTP status. Если Caddy access log уже включён:

```bash
sudo journalctl -u caddy -f -o cat | grep -E '"status":(4|5)[0-9]{2}'
```

Не менять production-конфигурацию логирования только ради нагрузочного запуска; k6 уже выводит `http_req_failed`, status checks и rates со стороны клиента.

Размер SQLite DB/WAL без открытия базы:

```bash
watch -n 2 'stat -c "%n %s bytes" /opt/honestserver/HonestLicenseFull.db /opt/honestserver/HonestLicenseFull.db-wal /opt/honestserver/HonestLicenseFull.db-shm 2>/dev/null'
```

Рост WAL в production-safe режиме не ожидается от самих тестовых запросов. Его рост во время read-only теста может указывать на параллельные записи реальных клиентов или фоновые операции.
