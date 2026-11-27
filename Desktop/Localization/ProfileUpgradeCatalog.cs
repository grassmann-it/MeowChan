namespace MeowChan.Desktop.Localization;

internal static class ProfileUpgradeCatalog
{
    private readonly record struct Translation(string Title, string Return, string Restore, string Failed);
    private readonly record struct SourceNotice(string Title, string Message, string ShowBackup, string Continue);

    internal static string Title => Texts.Own("profile.upgrade.title") ?? Current.Title;
    internal static string Return => Texts.Own("profile.upgrade.return") ?? Current.Return;
    internal static string Restore => Texts.Own("profile.upgrade.restore") ?? Current.Restore;
    internal static string Failed => Texts.Own("profile.upgrade.restoreFailed") ?? Current.Failed;

    internal static string SourceChangedTitle => ChangedSource.Title;
    internal static string SourceChangedMessage => ChangedSource.Message;
    internal static string ShowBackup => ChangedSource.ShowBackup;
    internal static string Continue => ChangedSource.Continue;

    private static SourceNotice ChangedSource => Texts.Language switch
    {
        "de" => new("Bisheriger Profilordner",
            "Der Profilordner von MeowChan 1.1.3 fehlt, ist nicht zugänglich oder hat sich geändert. Du kannst mit deinem Profil in 1.2.0 weiterarbeiten oder die Sicherung für die Rückkehr zu 1.1.3 ansehen.",
            "Sicherung anzeigen", "Weiterarbeiten"),
        "cs" => new("Původní složka profilu",
            "Složka profilu MeowChan 1.1.3 chybí, není přístupná nebo se změnila. Můžete pokračovat s profilem ve verzi 1.2.0 nebo si prohlédnout zálohu pro návrat k verzi 1.1.3.",
            "Zobrazit zálohu", "Pokračovat"),
        "es" => new("Carpeta del perfil anterior",
            "La carpeta del perfil de MeowChan 1.1.3 no está disponible o ha cambiado. Puedes seguir usando tu perfil en 1.2.0 o ver la copia de seguridad para volver a 1.1.3.",
            "Ver copia de seguridad", "Continuar"),
        "fr" => new("Ancien dossier du profil",
            "Le dossier du profil de MeowChan 1.1.3 est introuvable, inaccessible ou a changé. Vous pouvez continuer avec votre profil dans la version 1.2.0 ou consulter la sauvegarde pour revenir à la version 1.1.3.",
            "Voir la sauvegarde", "Continuer"),
        "it" => new("Cartella del profilo precedente",
            "La cartella del profilo di MeowChan 1.1.3 è assente, inaccessibile o è cambiata. Puoi continuare a usare il tuo profilo in 1.2.0 oppure vedere il backup per tornare alla versione 1.1.3.",
            "Mostra il backup", "Continua"),
        "ja" => new("以前のプロファイルフォルダー",
            "MeowChan 1.1.3 のプロファイルフォルダーが見つからないか、アクセスできないか、内容が変更されています。1.2.0 のプロファイルを引き続き使用するか、1.1.3 に戻すためのバックアップを表示できます。",
            "バックアップを表示", "続ける"),
        "ko" => new("이전 프로필 폴더",
            "MeowChan 1.1.3의 프로필 폴더가 없거나, 접근할 수 없거나, 변경되었습니다. 1.2.0에서 프로필을 계속 사용하거나 1.1.3으로 돌아가기 위한 백업을 볼 수 있습니다.",
            "백업 보기", "계속"),
        "pl" => new("Poprzedni folder profilu",
            "Folder profilu MeowChan 1.1.3 jest niedostępny lub się zmienił. Możesz nadal korzystać z profilu w wersji 1.2.0 albo wyświetlić kopię zapasową potrzebną do powrotu do wersji 1.1.3.",
            "Pokaż kopię zapasową", "Kontynuuj"),
        "pt-BR" => new("Pasta do perfil anterior",
            "A pasta do perfil do MeowChan 1.1.3 está ausente, inacessível ou foi alterada. Você pode continuar com seu perfil na versão 1.2.0 ou ver o backup para voltar à versão 1.1.3.",
            "Ver backup", "Continuar"),
        "ru" => new("Прежняя папка профиля",
            "Папка профиля MeowChan 1.1.3 отсутствует, недоступна или изменилась. Вы можете продолжить работу с профилем в версии 1.2.0 или посмотреть резервную копию для возврата к версии 1.1.3.",
            "Показать резервную копию", "Продолжить"),
        "zh-Hans" => new("旧版配置文件夹",
            "MeowChan 1.1.3 的配置文件夹已丢失、无法访问或发生了变更。你可以继续使用 1.2.0 的配置，也可以查看用于返回 1.1.3 的备份。",
            "查看备份", "继续"),
        _ => new("Previous profile folder",
            "The MeowChan 1.1.3 profile folder is missing, inaccessible, or has changed. You can continue with your 1.2.0 profile or view the backup for returning to 1.1.3.",
            "View backup", "Continue")
    };

    private static Translation Current => Texts.Language switch
    {
        "de" => new("Zu MeowChan 1.1.3 zurückkehren",
            "Stelle die Sicherung vom {0} in einem neuen Ordner wieder her. Öffne diesen Ordner danach mit MeowChan 1.1.3. Änderungen aus 1.2.0 bleiben im Profil dieser Version.\n\nSicherungsordner\n{1}",
            "Kopie wiederherstellen…", "Profil konnte nicht wiederhergestellt werden"),
        "cs" => new("Návrat k MeowChan 1.1.3",
            "Obnovte zálohu z {0} do nové složky. Poté ji otevřete v MeowChan 1.1.3. Změny z verze 1.2.0 zůstanou v jejím profilu.\n\nSložka zálohy\n{1}",
            "Obnovit kopii…", "Profil se nepodařilo obnovit"),
        "es" => new("Volver a MeowChan 1.1.3",
            "Restaura la copia de seguridad del {0} en una carpeta nueva y ábrela con MeowChan 1.1.3. Los cambios realizados en la versión 1.2.0 se conservan en su perfil.\n\nCarpeta de la copia de seguridad\n{1}",
            "Restaurar una copia…", "No se pudo restaurar el perfil"),
        "fr" => new("Revenir à MeowChan 1.1.3",
            "Restaurez la sauvegarde du {0} dans un nouveau dossier, puis ouvrez-le avec MeowChan 1.1.3. Les modifications effectuées dans la version 1.2.0 restent dans son profil.\n\nDossier de sauvegarde\n{1}",
            "Restaurer une copie…", "Impossible de restaurer le profil"),
        "it" => new("Torna a MeowChan 1.1.3",
            "Ripristina il backup del {0} in una nuova cartella, quindi aprila con MeowChan 1.1.3. Le modifiche apportate nella versione 1.2.0 rimangono nel relativo profilo.\n\nCartella del backup\n{1}",
            "Ripristina una copia…", "Impossibile ripristinare il profilo"),
        "ja" => new("MeowChan 1.1.3 に戻る",
            "{0} のバックアップを新しいフォルダーに復元し、そのフォルダーを MeowChan 1.1.3 で開いてください。1.2.0 で行った変更は、そのバージョンのプロファイルに残ります。\n\nバックアップフォルダー\n{1}",
            "コピーを復元…", "プロファイルを復元できませんでした"),
        "ko" => new("MeowChan 1.1.3으로 돌아가기",
            "{0}의 백업을 새 폴더에 복원한 다음 MeowChan 1.1.3에서 그 폴더를 여세요. 1.2.0에서 변경한 내용은 해당 버전의 프로필에 남습니다.\n\n백업 폴더\n{1}",
            "사본 복원…", "프로필을 복원하지 못했습니다"),
        "pl" => new("Wróć do MeowChan 1.1.3",
            "Przywróć kopię zapasową z {0} do nowego folderu, a następnie otwórz ten folder w MeowChan 1.1.3. Zmiany wprowadzone w wersji 1.2.0 pozostaną w jej profilu.\n\nFolder kopii zapasowej\n{1}",
            "Przywróć kopię…", "Nie udało się przywrócić profilu"),
        "pt-BR" => new("Voltar para o MeowChan 1.1.3",
            "Restaure o backup de {0} em uma nova pasta e abra essa pasta com o MeowChan 1.1.3. As alterações feitas na versão 1.2.0 ficam no perfil dessa versão.\n\nPasta do backup\n{1}",
            "Restaurar uma cópia…", "Não foi possível restaurar o perfil"),
        "ru" => new("Вернуться к MeowChan 1.1.3",
            "Восстановите резервную копию от {0} в новую папку и откройте её в MeowChan 1.1.3. Изменения, сделанные в версии 1.2.0, останутся в её профиле.\n\nПапка резервной копии\n{1}",
            "Восстановить копию…", "Не удалось восстановить профиль"),
        "zh-Hans" => new("返回 MeowChan 1.1.3",
            "将 {0} 的备份还原到新文件夹，然后用 MeowChan 1.1.3 打开该文件夹。1.2.0 中的更改会保留在该版本的配置中。\n\n备份文件夹\n{1}",
            "还原副本…", "无法还原配置"),
        _ => new("Return to MeowChan 1.1.3",
            "Restore the snapshot from {0} into a new folder, then open that folder with MeowChan 1.1.3. Changes made in 1.2.0 stay in its profile.\n\nSnapshot folder\n{1}",
            "Restore a copy…", "Profile restore failed")
    };
}
