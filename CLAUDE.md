# ops-bot

Telegram-бот керування власними сервісами й стандарт їх розгортання. Опис — у [README.md](README.md).

## Домовленості

- Бот — .NET 10, ASP.NET Core minimal API, webhook, Telegram.Bot; без БД — стан у файлах `data/`.
- Бот працює без прав root. Усе, що потребує root, робить `deploy/bin/ops-runner.sh` за файлами запитів; кожне поле запиту перевіряється там.
- `template/deploy.sh`, `template/setup-service.sh`, `template/changes.sh` — еталон стандарту; сервіси копіюють їх без змін. Будь-яка зміна шаблону — з тестом у `template/tests/deploy.test.sh` (WSL, root) і записом у README.md.
- Формат `@result <код>` і коди завершення `deploy.sh` — контракт з ботом (`Texts.Reason`, `Cards.Result`). Новий код — в обох місцях і в README.md §4.
- Тексти бота — українською й англійською (`Chat/Texts.cs`); коментарі в коді — англійською (ASD-STE100).
- Повідомлення комітів — англійською; зміни, видимі адміністратору, — трейлерами `Change-uk:` і `Change-en:` (README.md §3).
- Залежності — останні стабільні версії, перевірені на nuget.org.
