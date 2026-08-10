# HonestLicenseServer API — полная документация

Актуально для production API на 10 августа 2026 года.

- Base URL: `https://api.honestflow.ru`
- Swagger UI: `https://api.honestflow.ru/swagger`
- OpenAPI JSON: `https://api.honestflow.ru/swagger/v1/swagger.json`
- Production-БД: `/opt/honestserver/HonestLicenseFull.db`
- Формат времени: UTC, ISO 8601
- Формат JSON: имена полей в `camelCase`

## 1. Общая схема

```text
HonestFlow ── Bearer token ──> публичное клиентское API
HonestDesk ── X-Admin-Key ──> административное API
Сайт ─────── без авторизации ─> заявка на подключение
                                  │
                                  ▼
                         HonestLicenseFull.db
                                  │
                ссылки на файлы ──┴──> Яндекс.Диск
```

API работает с SQLite. Старые `ips_encrypted.json`, `versions.json` и
`licenses.json` во время обычной работы не читаются. Их данные были перенесены
в БД. `.exe`, `.msi` и `.zip` остаются на Яндекс.Диске; в SQLite хранятся только
их метаданные и ссылки.

## 2. Авторизация

### 2.1 Bearer-токен HonestFlow

HonestFlow получает `accessToken` и `refreshToken` через
`POST /api/auth/login`.

Успешный `TokenResponse` также содержит `clientId` и `clientName` для
идентификации клиента. Эти поля возвращаются и pending-устройству, но не
меняют его разрешения: конфигурация и лицензия по-прежнему недоступны.

```http
Authorization: Bearer ACCESS_TOKEN
```

- access token действует 15 минут;
- refresh token действует 30 дней;
- при refresh старый refresh token отзывается;
- при logout текущая сессия отзывается;
- в БД хранятся только SHA-256-хеши токенов;
- pending-устройство получает ограниченную сессию;
- конфигурация и лицензия доступны только активному зарегистрированному
  устройству.

### 2.2 Административный ключ HonestDesk

Все маршруты `/api/admin/*` требуют заголовок:

```http
X-Admin-Key: ADMIN_KEY
```

Ключ хранится только в локальном production-конфиге сервера и не должен
попадать в Git.

### 2.3 Ошибки

Большинство клиентских ошибок возвращается в формате Problem Details:

```json
{
  "type": "https://api.honestflow.ru/problems/invalid-access-token",
  "title": "Invalid access token",
  "status": 401,
  "instance": "/api/license/current",
  "code": "invalid_access_token",
  "traceId": "..."
}
```

Основные статусы:

- `400` — неверный JSON или валидация;
- `401` — отсутствующая/неверная авторизация;
- `403` — клиент или устройство выключены, либо устройство pending;
- `404` — объект не найден;
- `409` — конфликт состояния или дубликат;
- `410` — лицензия истекла или отозвана;
- `429` — rate limit;
- `500/503` — серверная ошибка или отсутствующая конфигурация ключа.

## 3. Сценарий HonestFlow

```text
1. HonestFlow создаёт постоянный deviceId установки.
2. Отправляет password + deviceId в POST /api/auth/login.
3. Для известного активного устройства получает обычную сессию.
4. Для неизвестного устройства получает ограниченную сессию и
   deviceRegistrationRequired=true.
5. HonestFlow спрашивает у пользователя название ПК и физический адрес точки.
6. Отправляет POST /api/device/request.
7. HonestDesk одобряет или отклоняет заявку.
8. После одобрения refresh связывает сессию с зарегистрированным устройством.
9. HonestFlow получает конфигурацию и подписанную лицензию.
```

## 4. Auth API

### POST `/api/auth/login`

Авторизация клиента по паролю-идентификатору и Device ID. Отдельного поля
`login` в запросе нет.

Авторизация: не требуется.

Rate limit:

- 10 запросов в минуту с одного IP;
- дополнительно не более 20 попыток за 5 минут для одного идентификатора.

Запрос:

```json
{
  "password": "HF-4K7P-X92M-R7DQ",
  "deviceId": "dcfc2927-7667-4c80-8b9f-2c47b29d4240"
}
```

Принимает:

- `password` — 1–256 символов, обязательное поле;
- `deviceId` — 1–128 символов, обязательное поле.

Откуда берёт данные:

- ищет `password` в `ClientSettings.IdentificationCode`;
- дополнительно проверяет PBKDF2-хеш в `Credentials.PasswordHash`;
- проверяет `Clients.IsActive`;
- ищет устройство по `Clients.Id + Devices.ExternalDeviceId`.

Что сохраняет:

- новую сессию в `RefreshTokens`;
- сохраняет только хеши access/refresh tokens;
- неизвестному устройству не создаёт заявку автоматически.

Ответ `200`:

```json
{
  "accessToken": "...",
  "refreshToken": "...",
  "expiresInSeconds": 900,
  "deviceRegistrationRequired": false,
  "clientId": "client-123",
  "clientName": "Название клиента"
}
```

Для неизвестного устройства `deviceRegistrationRequired` равен `true`.

Ошибки: `400`, `401 invalid_credentials`, `403 client_disabled`,
`403 device_disabled`, `429 rate_limit_exceeded`.

### POST `/api/auth/refresh`

Обновляет сессию и ротирует refresh token.

Авторизация: не требуется.

Rate limit: 30 запросов в минуту с одного IP.

Запрос:

```json
{
  "refreshToken": "..."
}
```

Откуда берёт: `RefreshTokens` по SHA-256-хешу токена, затем `Clients` и
`Devices`.

Что сохраняет:

- помечает старый refresh token отозванным (`rotated`);
- создаёт новую строку `RefreshTokens` в том же семействе;
- сохраняет связь с устройством, если оно уже одобрено.

Ответ `200`: такой же `TokenResponse`, как у login.

Ошибки: `401 invalid_refresh_token`, `403 client_disabled`,
`403 device_disabled`, `429`.

### POST `/api/auth/logout`

Отзывает текущую сессию.

Авторизация: Bearer.

Тело: отсутствует.

Что сохраняет: `RefreshTokens.RevokedAtUtc`, причина `logout`.

Ответ: `204 No Content`.

## 5. Регистрация устройств

### POST `/api/device/request`

Создаёт заявку на регистрацию неизвестного устройства.

Авторизация: Bearer активного клиента; зарегистрированное устройство не
обязательно.

Запрос:

```json
{
  "deviceId": "dcfc2927-7667-4c80-8b9f-2c47b29d4240",
  "name": "Касса 1",
  "address": "Омск, ул. Советская, 31"
}
```

Принимает:

- `deviceId` — 1–128 символов, должен совпадать с Device ID сессии;
- `name` — 1–200 символов;
- `address` — 1–300 символов, обязательный физический адрес торговой точки,
  не IP-адрес.

Откуда берёт: Client ID и Device ID из Bearer-сессии; проверяет `Devices` и
существующие `DeviceRegistrationRequests`.

Где хранит: `DeviceRegistrationRequests` (`RequestedName`,
`RequestedAddress`, `Status=Pending`, время создания).

Ответ `202`:

```json
{
  "id": 123,
  "status": "Pending",
  "requestedAtUtc": "2026-08-10T12:00:00Z"
}
```

Повторный запрос для уже существующей завершённой заявки возвращает её
текущее состояние. Ошибки: `400 device_id_does_not_match_token`,
`409 device_already_registered`, `401`, `403`.

### GET `/api/device/registration/current`

Возвращает статус заявки текущего устройства.

Авторизация: Bearer активного клиента.

Откуда берёт: `DeviceRegistrationRequests`; если заявки нет, но устройство
уже зарегистрировано — `Devices`.

Ответ `200`:

```json
{
  "deviceId": "...",
  "status": "Pending",
  "requestedAtUtc": "2026-08-10T12:00:00Z",
  "resolvedAtUtc": null,
  "comment": null
}
```

Статусы: `Pending`, `Approved`, `Rejected`. Если нет ни устройства, ни заявки:
`404 device_request_not_found`.

## 6. Конфигурация HonestFlow

### GET `/api/configuration/current`

Возвращает всю рабочую конфигурацию клиента и устройства одним запросом.

Авторизация: Bearer активного зарегистрированного устройства.

Откуда берёт:

- `Clients` — клиент;
- `Devices` — текущее устройство и физический адрес;
- `ClientSettings` — пароль-идентификатор, токен ЧЗ, RuDesktop;
- `LicensePolicies` — ограничения лицензии;
- `AppVersions` — глобальные версии;
- `ClientComponentVersions` — overrides клиента;
- `ComponentAssets` — файл, ссылка, SHA-256 и размер.

Правило версии:

```text
effectiveVersion = overrideVersion ?? globalVersion
```

Ответ `200`:

```json
{
  "configurationRevision": "2026-08-10T12:00:00Z",
  "client": {
    "clientId": "...",
    "name": "ООО Ромашка",
    "architecture": "x64",
    "hasLmDatabaseBackup": true,
    "ruDesktopEnabled": false,
    "ruDesktopAutoOfferPasswordSetup": false,
    "identificationCode": "HF-...",
    "chzToken": "..."
  },
  "device": {
    "deviceId": "...",
    "name": "Касса 1",
    "address": "Омск, ул. Советская, 31",
    "status": "Active"
  },
  "licensePolicy": {
    "isEnabled": true,
    "minimumHonestFlowVersion": "2.6.2.0",
    "offlineGraceHours": 72,
    "sourceRevision": 101,
    "sourceValidUntilUtc": "2027-08-10T08:19:27Z"
  },
  "components": [
    {
      "component": "HonestFlow",
      "globalVersion": "2.6.2.0",
      "overrideVersion": null,
      "effectiveVersion": "2.6.2.0",
      "fileName": "HonestFlow-2.6.2.0.msi",
      "downloadUrl": "https://disk.yandex.ru/...",
      "sha256": "...",
      "sizeBytes": 48234496,
      "architecture": "any",
      "isOverride": false
    }
  ]
}
```

`chzToken` является чувствительным значением и должен использоваться только
авторизованным HonestFlow. Ошибки: `401`, `403 client_disabled`,
`403 device_pending`, `403 device_disabled`.

## 7. Лицензия HonestFlow

### GET `/api/license/current`

Возвращает текущий персональный подписанный grant устройства.

Авторизация: Bearer активного зарегистрированного устройства.

Откуда берёт: `Licenses` по клиенту и устройству. Выбираются только записи:

- `SignatureScope = PersonalGrant`;
- подпись была проверена сервером (`SignatureVerifiedAtUtc` заполнено);
- максимальная `Revision` для пары клиент/устройство.

Ответ `200`:

```json
{
  "grantBase64": "...",
  "signatureBase64": "...",
  "keyId": "primary-2026",
  "revision": 101001,
  "issuedAtUtc": "2026-08-10T08:19:27Z",
  "validUntilUtc": "2027-08-10T08:19:27Z"
}
```

Сервер возвращает исходные подписанные байты, не пересобирая JSON. HonestFlow
декодирует `grantBase64`, проверяет ECDSA P-256/SHA-256 через встроенный
`public.pem`, затем читает grant.

Ответ содержит `ETag`. С `If-None-Match` сервер может вернуть `304`.

Ошибки:

- `404 license_not_found`;
- `410 license_revoked`;
- `410 license_expired`;
- `401/403`.

Офлайн: HonestFlow должен локально кэшировать исходные grant-байты, подпись и
`keyId`. Проверка подписи выполняется локально. Работа ограничена
`validUntilUtc` и правилами внутри подписанного grant.

## 8. Версии

### GET `/api/version/current/{application}`

Публично возвращает глобальную версию одного приложения.

Авторизация: не требуется.

Пример: `GET /api/version/current/HonestFlow`.

Откуда берёт: `AppVersions` по точному имени `Application`.

Ответ `200`:

```json
{
  "application": "HonestFlow",
  "currentVersion": "2.6.2.0",
  "importedAtUtc": "2026-08-07T12:40:46Z"
}
```

Endpoint не сканирует Яндекс.Диск и не возвращает список всех приложений.
Ошибка: `404 application_not_found`.

## 9. Поддержка

### POST `/api/support/requests`

Создаёт обращение из HonestFlow.

Авторизация: Bearer активного клиента; разрешено pending-устройству.

Rate limit: 5 обращений в час на клиента.

Запрос:

```json
{
  "subject": "Не запускается модуль",
  "message": "Подробное описание проблемы",
  "contact": "+7 999 000-00-00",
  "honestFlowVersion": "2.6.2.0"
}
```

Ограничения: subject 3–200, message 3–5000, contact 3–300, версия до 100.

Откуда берёт клиента и устройство: из Bearer-сессии.

Где хранит: `SupportRequests`, статус `Accepted`.

Ответ `202`:

```json
{
  "id": 15,
  "status": "Accepted",
  "createdAtUtc": "2026-08-10T12:00:00Z"
}
```

## 10. Публичная заявка с сайта

### POST `/api/connection-requests`

Принимает заявку на подключение HonestFlow с сайта.

Авторизация: не требуется.

Rate limit: 5 заявок за 10 минут с одного IP. Максимальный body: 16 КБ.

Запрос:

```json
{
  "contactName": "Иван Иванов",
  "company": "ООО Ромашка",
  "phone": "+7 999 000-00-00",
  "email": "ivan@example.ru",
  "city": "Омск",
  "workplaceCount": 12,
  "inventorySystem": "1С",
  "comment": "Хотим подключить сеть магазинов",
  "website": "",
  "source": "honestflow-site"
}
```

Обязательные поля: `contactName`, `phone`, `workplaceCount` (1–100000).
`website` — honeypot. Если оно заполнено, сервер возвращает `204`, не сохраняет
заявку и не отправляет письмо.

Где хранит: `ConnectionRequests`, включая IP, User-Agent, статус уведомления и
ошибку SMTP.

После сохранения пытается отправить письмо на настроенный адрес. Ошибка SMTP не
удаляет заявку и не меняет успешный HTTP-ответ.

Ответ `201`:

```json
{
  "success": true,
  "requestId": 123
}
```

## 11. Admin API: клиенты

Все маршруты этого и последующих административных разделов требуют
`X-Admin-Key`.

### GET `/api/admin/clients`

Возвращает всех клиентов, количество устройств и лицензий.

Читает: `Clients`, `Devices`, `Licenses`, `Credentials`.

```json
[
  {
    "clientId": "...",
    "name": "ООО Ромашка",
    "inn": "5500000000",
    "architecture": "x64",
    "isActive": true,
    "hasLmDatabaseBackup": true,
    "deviceCount": 12,
    "activeDeviceCount": 11,
    "licenseCount": 11,
    "credentialConfigured": true
  }
]
```

### GET `/api/admin/clients/{clientId}`

Возвращает одного клиента с датами, количеством устройств и лицензий.
`404 client_not_found`, если клиент отсутствует.

### POST `/api/admin/clients`

Создаёт клиента, credential и интеграционные настройки.

```json
{
  "clientId": "external-client-id",
  "name": "ООО Ромашка",
  "login": "internal-unique-login",
  "password": "HF-4K7P-X92M-R7DQ",
  "inn": "5500000000",
  "architecture": "x64",
  "hasLmDatabaseBackup": false,
  "chzToken": "token-chz"
}
```

`login` здесь является внутренним уникальным полем `Credentials`; HonestFlow
его при авторизации не отправляет.

Сохраняет:

- `Clients`;
- `Credentials` с PBKDF2-хешем пароля;
- `ClientSettings.IdentificationCode` с самим паролем-идентификатором;
- `ClientSettings.ChzToken`;
- `AuditEvents`.

Ответ `201`: `{ "id": 1, "clientId": "..." }`.

### PUT `/api/admin/clients/{clientId}`

Обновляет имя, ИНН, архитектуру, активность и признак LM backup.

```json
{
  "name": "ООО Ромашка",
  "inn": "5500000000",
  "architecture": "x64",
  "isActive": true,
  "hasLmDatabaseBackup": false
}
```

При выключении клиента отзывает все его активные сессии. Ответ `204`.

### GET `/api/admin/clients/{clientId}/integration-settings`

Читает `ClientSettings` и возвращает пароль-идентификатор и токен ЧЗ:

```json
{
  "clientId": "...",
  "identificationCode": "HF-...",
  "chzToken": "...",
  "isConfigured": true
}
```

Ответ содержит чувствительные данные и предназначен только для HonestDesk.

### PUT `/api/admin/clients/{clientId}/integration-settings`

```json
{
  "identificationCode": "HF-NEW-CODE",
  "chzToken": "new-chz-token"
}
```

Обновляет `ClientSettings` и заново создаёт PBKDF2-хеш во всех активных
`Credentials` клиента. Ответ `204`.

## 12. Admin API: устройства и заявки

### GET `/api/admin/devices?clientId={clientId}`

Возвращает все устройства либо устройства одного клиента.

```json
[
  {
    "id": 10,
    "clientId": "...",
    "clientName": "ООО Ромашка",
    "deviceId": "...",
    "name": "Касса 1",
    "address": "Омск, ул. Советская, 31",
    "comment": null,
    "status": "Active",
    "registeredAtUtc": "2026-08-10T12:00:00Z"
  }
]
```

### POST `/api/admin/devices`

Ручное создание активного устройства.

```json
{
  "clientId": "...",
  "deviceId": "...",
  "name": "Касса 1",
  "address": "Омск, ул. Советская, 31",
  "comment": "Основная касса"
}
```

Сохраняет `Devices` и `AuditEvents`. Ответ `201`.

### PUT `/api/admin/devices/{id}`

```json
{
  "name": "Касса 1",
  "address": "Омск, ул. Советская, 31",
  "comment": "Основная касса",
  "status": "Active"
}
```

Допустимые статусы: `Active`, `Disabled`, `Deleted`. При любом статусе кроме
`Active` отзывает активные сессии устройства. Ответ `204`.

### GET `/api/admin/device-requests?status=Pending`

Возвращает заявки из `DeviceRegistrationRequests`. Без query-параметра по
умолчанию показывает только `Pending`. Пустой `status` показывает все.

Ответ содержит `requestedName`, `requestedAddress`, статус, даты и комментарий.

### PUT `/api/admin/device-requests/{id}/approve`

```json
{
  "name": null,
  "address": null,
  "comment": "Проверено"
}
```

Если name/address не переданы, используются `RequestedName` и
`RequestedAddress` из заявки. Создаёт `Devices`, переводит заявку в `Approved`
и связывает ожидающие сессии с устройством. Ответ `200`: `{ "id": 10 }`.

### PUT `/api/admin/device-requests/{id}/reject`

```json
{
  "comment": "Устройство не подтверждено"
}
```

Переводит заявку в `Rejected`. Ответ `204`.

## 13. Admin API: лицензии

### GET `/api/admin/licenses?clientId={clientId}`

Возвращает метаданные всех лицензий либо лицензий одного клиента. Читает
`Licenses`, `Clients`, `Devices`.

Основные поля: `id`, `clientId`, `clientName`, `deviceId`, `revision`, `keyId`,
`signatureScope`, `signatureVerifiedAtUtc`, `status`, `issuedAtUtc`,
`validUntilUtc`, `hasSignature`.

### GET `/api/admin/licenses/{id}`

Возвращает полную запись лицензии, включая `grantJson` и `signatureBase64`.

### POST `/api/admin/licenses`

Публикует персональный grant, сформированный и подписанный HonestDesk.

```json
{
  "grantBase64": "BASE64_UTF8_JSON_BYTES",
  "signatureBase64": "BASE64_ECDSA_SIGNATURE",
  "keyId": "primary-2026"
}
```

Минимальные поля внутри декодированного grant:

```json
{
  "revision": 101001,
  "clientId": "...",
  "deviceId": "...",
  "issuedAtUtc": "2026-08-10T08:19:27Z",
  "validUntilUtc": "2027-08-10T08:19:27Z"
}
```

Сервер:

1. декодирует исходные UTF-8 байты;
2. находит public key по `keyId`;
3. проверяет ECDSA P-256/SHA-256;
4. проверяет клиента, устройство и уникальность revision;
5. помечает старые активные grants пары как `Superseded`;
6. сохраняет исходные байты, JSON, подпись и метаданные в `Licenses`;
7. создаёт `AuditEvents`.

Закрытый ключ никогда не передаётся серверу. Ответ `201`: `{ "id": 123 }`.

### PUT `/api/admin/licenses/{id}/revoke`

Меняет `Status` активной лицензии на `Revoked`, пишет аудит. Ответ `204`.

## 14. Admin API: версии, assets и overrides

### GET `/api/admin/versions`

Возвращает сразу все глобальные версии из `AppVersions`.

```json
[
  {
    "id": 1,
    "application": "HonestFlow",
    "currentVersion": "2.6.2.0",
    "importedAtUtc": "2026-08-07T12:40:46Z"
  }
]
```

### PUT `/api/admin/versions/{application}`

Создаёт или обновляет глобальную текущую версию.

```json
{
  "currentVersion": "2.7.0.0"
}
```

Сохраняет `AppVersions` и `AuditEvents`. Ответ `204`.

### GET `/api/admin/assets?component={component}&architecture={architecture}`

Возвращает каталог всех известных файлов либо файлов одного компонента.

```json
[
  {
    "component": "HonestFlow",
    "version": "2.6.2.0",
    "architecture": "any",
    "fileName": "HonestFlow-2.6.2.0.msi",
    "downloadUrl": null,
    "yandexPublicKey": "https://disk.360.yandex.ru/d/...",
    "yandexPath": "/2.6.2.0/HonestFlow.exe",
    "sha256": "0123456789abcdef...",
    "sizeBytes": 48234496,
    "updatedAtUtc": "2026-08-10T12:00:00Z"
  }
]
```

Читает `ComponentAssets`. `architecture` принимает `x86`, `x64`, `arm64` или
`any`. Без фильтра возвращаются все архитектуры. Endpoint не просматривает
Яндекс.Диск при каждом GET: каталог заполняется административным импортёром.

### PUT `/api/admin/assets/{component}/{version}`

Создаёт или обновляет метаданные файла.

```json
{
  "fileName": "HonestFlow-2.6.2.0.msi",
  "architecture": "any",
  "downloadUrl": null,
  "yandexPublicKey": "https://disk.360.yandex.ru/d/...",
  "yandexPath": "/2.6.2.0/HonestFlow.exe",
  "sha256": "64-символьный-hex-sha256",
  "sizeBytes": 48234496
}
```

Нужно указать либо прямой `downloadUrl`, либо пару `yandexPublicKey` и
`yandexPath`. `architecture` по умолчанию `any`; варианты x32/win32
нормализуются в `x86`, варианты win64/amd64 — в `x64`. SHA-256 — 64
hex-символа. Сам файл в SQLite не загружается. Уникальность записи:
`component + version + architecture`. Сохраняет `ComponentAssets` и аудит.
Ответ `204`.

Импорт публичной папки выполняется локально. Dry-run:

```powershell
python scripts/import_yandex_assets.py `
  "https://disk.360.yandex.ru/d/sngNP8yBz9weWA"
```

Реальная запись добавляет `--apply`, после чего ключ API вводится скрыто и
требуется подтверждение `IMPORT`. Импортёр распознаёт HonestFlow, AtolDriver,
Controller, ESM и LmModule; JSON, licenses, backup-архивы и посторонние файлы
игнорируются.

### GET `/api/assets/{component}/{version}/download`

Защищённый endpoint скачивания для HonestFlow.

Авторизация: Bearer активного зарегистрированного устройства.

Сервер читает архитектуру клиента из `Clients`, выбирает точную запись
`ComponentAssets` (`x86`/`x64`/`arm64`) либо fallback `any`. Для прямого URL
сразу возвращает `302`. Для Яндекс.Диска запрашивает через официальный API
свежий временный `href` по сохранённым `YandexPublicKey + YandexPath`, затем
возвращает `302` на этот адрес. Временная ссылка в БД не хранится.

Ошибки: `404 component_asset_not_found`,
`404 component_asset_download_not_configured`, `502`, `401`, `403`.

### GET `/api/admin/clients/{clientId}/component-versions`

Возвращает все индивидуальные overrides клиента из
`ClientComponentVersions`.

### PUT `/api/admin/clients/{clientId}/component-versions/{component}`

Назначает или снимает override.

```json
{
  "requiredVersion": "2.5.9.0"
}
```

Указанная версия обязана существовать в `ComponentAssets`. `null` или пустая
строка удаляет override. Ответ `204`.

## 15. Admin API: обращения поддержки

### GET `/api/admin/support-requests?status={status}`

Возвращает обращения из `SupportRequests`. Без `status` возвращает все.

Поля: `id`, `clientId`, `externalDeviceId`, `subject`, `message`, `contact`,
`honestFlowVersion`, `status`, `createdAtUtc`.

Сейчас отдельного административного endpoint для изменения статуса обращения
нет.

## 16. Хранение данных

| Таблица | Что хранит | Кто читает/пишет |
|---|---|---|
| `Clients` | клиенты, ИНН, архитектура, активность | auth, configuration, admin |
| `Credentials` | внутренний login и PBKDF2-хеш идентификатора | auth, admin |
| `ClientSettings` | открытый identification code, токен ЧЗ, RuDesktop | auth, configuration, admin |
| `Devices` | Device ID, имя, физический адрес, статус | auth, configuration, admin |
| `DeviceRegistrationRequests` | заявки, запрошенные имя и адрес | HonestFlow, HonestDesk |
| `RefreshTokens` | хеши токенов и состояние сессий | auth |
| `Licenses` | исходные grant-байты, JSON, подпись, keyId, статус | HonestFlow, HonestDesk |
| `LicensePolicies` | версия, offline grace, срок политики | configuration |
| `AppVersions` | глобальная версия каждого компонента | version, configuration, admin |
| `ComponentAssets` | компонент, версия, архитектура, файл, public key/path Яндекса, SHA-256, размер | configuration, assets download, admin |
| `ClientComponentVersions` | overrides версий клиента | configuration, admin |
| `SupportRequests` | обращения из HonestFlow | HonestFlow, HonestDesk |
| `ConnectionRequests` | заявки с сайта и SMTP-статус | сайт/API |
| `AuditEvents` | административные изменения, IP, trace ID | сервер |

## 17. Что хранится не в SQLite

- исполняемые файлы `.exe`, `.msi`, `.zip` — на Яндекс.Диске;
- закрытый ECDSA-ключ — только локально у HonestDesk/оператора;
- публичный ECDSA-ключ — в HonestFlow и production-конфигурации API;
- Admin API key и SMTP password — в локальном production-конфиге;
- технические логи — в `systemd/journald`;
- офлайн-кэш лицензии — локально у HonestFlow после интеграции.

## 18. Логи и аудит

Технические логи API:

```bash
sudo journalctl -u honestserver -f
sudo journalctl -u honestserver -n 100 --no-pager
```

Административные изменения записываются в `AuditEvents`: действие, тип и ID
объекта, клиент, JSON-детали, IP и correlation ID. Публичного/admin endpoint для
чтения аудита пока нет.

## 19. Текущие ограничения

- `GET /api/version/current/{application}` возвращает только одно приложение;
- публичного списка всех версий нет;
- `assets` синхронизируется отдельным административным импортёром, а не при каждом клиентском запросе;
- файлы не загружаются через API;
- нет admin GET для `ConnectionRequests` и `AuditEvents`;
- нет endpoint изменения статуса `SupportRequests`;
- Swagger отражает маршруты и схемы, но часть admin-ответов пока описана
  анонимными DTO и имеет минимум пояснений.
