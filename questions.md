# HonestLicenseServer: вопросы и замечания перед интеграцией HonestFlow

Дата аудита: 2026-08-10.

Этот документ фиксирует текущее состояние API, найденные проблемы и решения, которые нужно согласовать до подключения Windows-клиента HonestFlow.

## Границы первого этапа

- HonestLicenseServer отвечает только за авторизацию, конфигурацию клиента, состояния устройств, лицензии, версии и, возможно, отправку обращений в поддержку.
- Установщики, архивы, RuDesktop и `HonestFlow.exe` остаются на Яндекс Диске.
- Server API не хранит и не отдаёт большие установочные файлы.
- Существующие endpoints сохраняются; изменения должны быть обратно совместимыми.

## Что уже работает

- `POST /api/auth/login` — вход по логину и паролю.
- `POST /api/auth/refresh` — ротация refresh token.
- `POST /api/auth/logout` — отзыв текущей сессии.
- Access token действует 15 минут, refresh token — 30 дней.
- В SQLite сохраняются только SHA-256-хеши токенов.
- Пароли хешируются PBKDF2-SHA256 с 210 000 итераций.
- Сессия содержит внутренние `ClientId`, `DeviceId` и внешний идентификатор устройства.
- Неизвестное устройство создаёт заявку `Pending`.
- Администратор может подтвердить или отклонить заявку.
- `GET /api/license/current` выбирает лицензию по паре ClientId + DeviceId из сессии, а не из параметров клиента.
- `GET /api/version/current/{application}` возвращает одну глобальную версию.
- Swagger доступен через `/swagger`.

## Критические замечания

### 1. Аутентификация

Сейчас Bearer token обрабатывает самодельный `TestBearerMiddleware`. Это не стандартная ASP.NET Core authentication scheme.

Нужно:

- перейти на стандартный authentication handler;
- формировать `ClaimsPrincipal`;
- использовать `[Authorize]` и authorization policies;
- централизованно проверять сессию, клиента и устройство;
- добавить Bearer security scheme в OpenAPI.

Состояние клиента и устройства должно проверяться не только во время login, но и при refresh и каждом защищённом запросе.

### 2. Ограничение попыток входа

Сейчас rate limiting отсутствует. Публичный `/api/auth/login` можно перебирать без серверного ограничения.

Нужен лимит одновременно по IP и нормализованному login, а также отдельные ограничения для refresh и отправки обращений.

### 3. Исходные байты лицензии

Сейчас при публикации `grantBase64` декодируется в UTF-8 string и сохраняется в `Licenses.GrantJson`.

Для ECDSA должны сохраняться исходные подписанные байты без повторной сериализации JSON.

Предлагаемое хранение:

```text
GrantBytes BLOB NOT NULL
```

API должен возвращать:

```json
{
  "grantBase64": "...",
  "signatureBase64": "...",
  "keyId": "primary-2026",
  "revision": 82,
  "issuedAtUtc": "...",
  "validUntilUtc": "..."
}
```

Перед миграцией необходимо проверить подписи существующих лицензий на байтах `Encoding.UTF8.GetBytes(GrantJson)`.

### 4. Состояния лицензии

Сейчас отсутствующая, просроченная и отозванная лицензии фактически сводятся к `404 active_license_not_found`.

Нужно различать:

- `404 license_not_found` — лицензия никогда не выпускалась;
- `410 license_expired` — лицензия просрочена;
- `410 license_revoked` — лицензия отозвана;
- `403 device_pending` — устройство ждёт подтверждения;
- `403 client_disabled`;
- `403 device_disabled`.

### 5. ETag

`GET /api/license/current` должен возвращать `ETag`. При совпадающем `If-None-Match` возвращается `304 Not Modified`.

Проверки токена, клиента и устройства должны выполняться до возврата `304`.

### 6. Production admin key

Dev-ключ `local-dev-admin-key-change-me` нельзя использовать на публичном сервере. `appsettings.Local.json` с секретом не должен попадать в production publish.

Admin key нужно заменить на длинный случайный секрет и хранить только в environment/systemd credentials.

## Клиентская конфигурация

В SQLite уже есть необходимые таблицы:

- `AppVersions` — глобальные версии;
- `ClientComponentVersions` — индивидуальные версии клиента;
- `Clients` — architecture и hasLmDatabaseBackup;
- `Devices` — имя и адрес точки;
- `ClientSettings` и `LicensePolicies` — дополнительные настройки.

Однако часть этих таблиц сейчас не сопоставлена в EF Core и не используется API.

Предлагаемый endpoint:

```text
GET /api/configuration/current
Authorization: Bearer <token>
```

Пример ответа:

```json
{
  "configurationRevision": "2026-08-10T10:00:00Z",
  "client": {
    "clientId": "external-client-id",
    "name": "ИП Кураев",
    "architecture": "x64",
    "hasLmDatabaseBackup": true
  },
  "device": {
    "deviceId": "external-device-id",
    "name": "DESKTOP-4GBH9SK",
    "address": "Адрес точки",
    "status": "active"
  },
  "components": [
    {
      "component": "LmModule",
      "globalVersion": "2.6.0-10",
      "overrideVersion": null,
      "effectiveVersion": "2.6.0-10",
      "fileName": "LmModule-2.6.0-10.zip"
    }
  ]
}
```

Правило версии:

```text
effectiveVersion = overrideVersion ?? globalVersion
```

API возвращает только имя и версию файла. Сам файл остаётся на Яндекс Диске.

Существующий `GET /api/version/current/{application}` нужно оставить для получения одной глобальной версии и мониторинга.

## Регистрация устройства

Предлагается добавить:

```text
GET /api/device/registration/current
```

Он позволит HonestFlow явно узнать состояние `pending`, `approved` или `rejected`, вместо постоянных попыток получить лицензию.

Нужно определить, может ли pending-устройство:

- получать refresh token;
- читать ограниченную конфигурацию;
- отправлять обращение в поддержку.

## Поддержка

SMTP-пароль нельзя передавать или встраивать в HonestFlow.

Если отправка обращений входит в первый этап, предлагается:

```text
POST /api/support/requests
Authorization: Bearer <token>
```

На первом этапе запрос содержит только тему, текст, контакт и версию HonestFlow. Файловые вложения не принимаются.

SMTP credentials хранятся только на сервере. Ответ — `202 Accepted` с идентификатором обращения.

## Единый формат ошибок

Все ошибки должны возвращаться как `ProblemDetails` с машинно-читаемым `code`:

```json
{
  "type": "https://api.honestflow.ru/problems/device-pending",
  "title": "Device confirmation is required",
  "status": 403,
  "detail": "The device is awaiting administrator approval.",
  "instance": "/api/license/current",
  "code": "device_pending",
  "traceId": "00-..."
}
```

Обязательные статусы контракта:

| HTTP | Назначение |
|---:|---|
| 400 | Невалидный JSON или поля |
| 401 | Неверные credentials или токен |
| 403 | Отключённый клиент/устройство или pending |
| 404 | Ресурс никогда не существовал |
| 409 | Конфликт revision или состояния |
| 410 | Отозванный/просроченный ресурс |
| 429 | Rate limit |
| 500 | Внутренняя ошибка без stack trace |

## OpenAPI и C#-клиент

Чтобы контракт подходил для генерации C#-клиента, необходимо:

- заменить анонимные ответы на именованные response DTO;
- использовать `ActionResult<TDto>`;
- описать все response status;
- добавить Bearer security scheme;
- отдельно описать `X-Admin-Key`;
- добавить ограничения длины и формата полей;
- добавить примеры;
- использовать стабильные string enum;
- зафиксировать уникальные `operationId`;
- добавить контрактный тест, который генерирует и компилирует C#-клиент.

## Контрактные тесты перед интеграцией

Минимально необходимы сценарии:

1. Login активного клиента и устройства.
2. Неверный пароль.
3. Отключённый клиент.
4. Отключённое устройство.
5. Новое pending-устройство.
6. Approve и reject устройства.
7. Ротация и повторное использование refresh token.
8. Logout.
9. Защита от получения чужой конфигурации и grant.
10. Глобальные версии без overrides.
11. Индивидуальный override одного компонента.
12. Отсутствующая, просроченная и отозванная лицензия.
13. Проверка исходных grant bytes и ECDSA.
14. ETag и `304 Not Modified`.
15. Единый ProblemDetails.
16. Генерация и компиляция C#-клиента из Swagger.

Тесты должны использовать отдельную временную SQLite-базу, а не production `.db`.

## Предлагаемый порядок реализации

1. Зафиксировать решения по вопросам ниже.
2. Добавить DTO, ProblemDetails, validation и OpenAPI-контракт.
3. Заменить тестовый Bearer middleware на стандартную authentication scheme.
4. Добавить rate limiting и централизованную проверку состояний.
5. Реализовать состояние регистрации устройства.
6. Реализовать `/api/configuration/current`.
7. Мигрировать лицензии на исходные `GrantBytes` и проверить существующие подписи.
8. Добавить ETag и точные состояния лицензирования.
9. При необходимости реализовать server-side support endpoint.
10. Сгенерировать C#-клиент и провести пилотную интеграцию HonestFlow.

## Вопросы, которые нужно решить

Отметку решения можно записывать прямо под каждым вопросом.

1. Как HonestFlow формирует стабильный `deviceId`: MachineGuid, сохранённый GUID или отпечаток оборудования?
2. Должен ли login автоматически создавать заявку нового устройства?
3. Может ли pending-устройство получать refresh token?
4. Может ли pending-устройство обращаться в поддержку?
5. Нужно ли различать состояния клиента `Disabled` и `Deleted`?
6. Что точно означает `Devices.Address`?
7. Допустимые architecture: только `x86`, `x64`, `arm64` или произвольная строка?
8. Как формируются имена файлов для LmModule, AtolDriver, ESM, Controller и HonestFlow?
9. Может ли override менять только версию или также имя файла?
10. Должен ли одиночный version endpoint оставаться публичным без Bearer token?
11. Где хранится публичный ECDSA-ключ и должен ли сервер проверять подпись при публикации?
12. Может ли существовать несколько активных лицензий для одной пары client/device?
13. Как HonestFlow должен вести себя в offline grace period?
14. Все ли license policy находятся внутри подписанного grant?
15. Нужна ли смена пароля на первом этапе?
16. Нужна ли функция «выйти со всех устройств»?
17. Входит ли endpoint поддержки в первый этап?
18. Какой генератор C#-клиента использовать: NSwag, Kiota или OpenAPI Generator?

## Решение, рекомендуемое по умолчанию

- Один долгоживущий `HttpClient` в HonestFlow.
- Opaque access/refresh tokens, refresh token хранится через Windows DPAPI.
- Pending-устройство получает ограниченную сессию и может проверять статус заявки.
- Рабочая конфигурация и лицензия доступны только после approve.
- `/api/configuration/current` возвращает клиента, устройство и effective versions одним запросом.
- Лицензия хранится как исходный BLOB и возвращается как `grantBase64`.
- HonestFlow продолжает локальную проверку ECDSA P-256/SHA-256.
- Бинарники и архивы остаются на Яндекс Диске.
- SMTP credentials остаются только на сервере.
