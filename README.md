# DonationMusic 

Простое приложение для стримов: YouTube-плеер с очередью видео, которая пополняется через DonationAlerts и DonateX и обновляется в реальном времени.

## Возможности
- Добавление YouTube-видео через донат
- Ручное добавление и пропуск видео
- Live-синхронизация через WebSocket
- Очередь сохраняется между перезапусками
- Есть возможность подключить WebHook

## Запуск
В релизе уже есть **готовый `.exe`** — установка не требуется.

1. Распаковать архив
2. Заполнить `config.json`
3. Установить NET 8
4. Запустить `DonationMusic.exe`  

## `config.json`

```json
{
  "ClientId": "DA_CLIENT_ID",
  "ClientSecret": "DA_CLIENT_SECRET",
  "RedirectUri": "http://localhost:5000/callback/",
  "DAWidgetToken": "DA_WIDGET_TOKEN"
}
```

## `WebHook`

```json
{
"title":"",
"url":""
}
```
