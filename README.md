# HonestLicenseServer

Полная русскоязычная документация API: [`docs/API-RU.md`](docs/API-RU.md).

Согласованные границы системы и решения перед интеграцией клиентов описаны в
[`docs/architecture-decisions.md`](docs/architecture-decisions.md).

Интеграционные тесты используют отдельную временную SQLite-базу:

```powershell
dotnet test HonestLicenseServer.slnx -c Release
```

Текущая лицензия поддерживает `ETag`/`If-None-Match`. Текстовые обращения
принимаются через `POST /api/support/requests`; SMTP и вложения на первом этапе
не используются.

Локальный сервер лицензирования на .NET 10, ASP.NET Core, EF Core и SQLite.

## База

API использует единую базу `../HonestLicenseFull.db`. Она содержит импортированные данные клиентов, настроек, устройств, лицензионных политик, версий и персональных signed grants, а также рабочие таблицы сессий, заявок и аудита.

## Запуск

```powershell
dotnet restore
dotnet run --urls http://localhost:5074
```

Локальный admin key задаётся в `appsettings.Local.json`. Этот файл исключён из Git. На Linux значение нужно передавать через переменную окружения `AdminApi__Key`.

## HonestFlow API

```text
POST /api/auth/login
POST /api/auth/refresh
POST /api/auth/logout
POST /api/device/request
GET  /api/license/current
GET  /api/version/current/{application}
```

Тело `POST /api/auth/login` не содержит отдельного логина:

```json
{
  "password": "HF-...",
  "deviceId": "installation-guid"
}
```

Если устройство неизвестно, сервер возвращает ограниченную сессию с
`deviceRegistrationRequired: true`, но не создаёт заявку автоматически.
HonestFlow должен явно запросить физический адрес торговой точки и отправить:

```json
{
  "deviceId": "installation-guid",
  "name": "Касса 1",
  "address": "Омск, ул. Примерная, 10"
}
```

в `POST /api/device/request`. Поле `address` обязательное и означает физический
адрес торговой точки, а не IP-адрес.

Access token живёт 15 минут, refresh token — 30 дней. В базе хранятся только хеши токенов. Refresh token ротируется при каждом обновлении; logout отзывает текущую сессию.

Неизвестное устройство автоматически создаёт заявку `Pending`. Оно не получает лицензию до подтверждения администратором.

## HonestDesk Admin API

Каждый запрос требует заголовок:

```text
X-Admin-Key: local-dev-admin-key-change-me
```

Чтение:

```text
GET /api/admin/clients
GET /api/admin/clients/{clientId}
GET /api/admin/devices?clientId={clientId}
GET /api/admin/licenses?clientId={clientId}
GET /api/admin/licenses/{id}
GET /api/admin/versions
GET /api/admin/device-requests?status=Pending
```

Изменение:

```text
POST /api/admin/clients
PUT  /api/admin/clients/{clientId}
POST /api/admin/devices
PUT  /api/admin/devices/{id}
POST /api/admin/licenses
PUT  /api/admin/versions/{application}
PUT  /api/admin/device-requests/{id}/approve
PUT  /api/admin/device-requests/{id}/reject
```

Signed grant публикуется как `grantBase64` вместе с `signatureBase64`. Это сохраняет точные подписанные байты без изменения форматирования JSON.

## Перед размещением на сервере

- заменить локальный admin key и вынести его в переменную окружения;
- добавить rate limiting входа;
- настроить HTTPS, резервное копирование и журналирование;
- добавить ротацию административных ключей;
- оформить изменения схемы через миграции EF Core.
