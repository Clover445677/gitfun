# Резервные копии настроек Windows Update

Эта папка содержит экспортированные ветки реестра Windows, относящиеся к службам обновления. Это вспомогательные резервные копии, а не самостоятельное приложение.

| Файл | Ветка реестра |
| --- | --- |
| BITS.reg | HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BITS |
| DoSvc.reg | HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DoSvc |
| UsoSvc.reg | HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\UsoSvc |
| wuauserv.reg | HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wuauserv |

Файлы сохранены для возможного восстановления исходных настроек. Точная причина их создания в репозитории не документирована.

Это данные с конкретного компьютера. Импорт .reg изменяет системный реестр; не запускайте эти файлы просто для просмотра. Их можно открыть в текстовом редакторе.
