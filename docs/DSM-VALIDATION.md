# Overenie na fyzickom DSM 7.3

Tento build zatiaľ nebol testovaný na fyzickom NAS. Nasledujúce kroky vykonajte s malým testovacím priečinkom a nepodstatnými súbormi pred otvorením verejného vstupu.

1. Overte DNS a TLS z kontajnera na nakonfigurovaný DSM hostname. Dôveryhodný certifikát sa musí overiť bez výnimiek. Skúste nesprávny hostname/certifikát: upstream musí zlyhať.
2. Vytvorte verejné download odkazy pre jeden súbor a priečinok s vnoreným priečinkom. Overte zoznam, návrat cez breadcrumbs, jednu položku, viac položiek, ZIP a celý vnorený priečinok.
3. Vytvorte download odkaz s heslom. Overte nesprávne heslo, správne heslo, následný zoznam/download a izoláciu v druhom súkromnom okne. Druhé okno musí heslo vyžiadať znova. Potvrďte, že reálne DSM Set-Cookie umožní Initdata a ďalšie API; HAR exporty tieto hlavičky vynechali.
4. Vytvorte anonymný File Request bez hesla a potom s heslom. Nahrajte ASCII a Unicode názov, prázdny súbor a viac súborov. Overte cieľ v DSM. Iný návštevník nesmie vidieť zoznam odoslaných súborov.
5. Nahrajte rovnaký názov dvakrát a potvrďte bezpečné správanie `overwrite=false`. Zrušte prenos a skontrolujte, či DSM ponechalo čiastočný súbor; proxy nemá mazacie oprávnenie ani endpoint.
6. Zrušte/expirujte odkaz po otvorení stránky a pred downloadom/uploadom. Operácia musí zlyhať; stránka nesmie pokračovať zo starej lokálnej evidencie. Skúste future `start_at`.
7. Overte configured limit a veľký stream pri sledovaní pamäte. Otestujte prerušené spojenie, pomalého klienta, timeout, plný cieľový zväzok a DSM restart. Prečíslovanie relácie po reštarte proxy musí opäť vyžiadať heslo.
8. Overte verejnú doménu: `/webapi/auth.cgi`, `/webman/`, `/api/proxy` a neznáme endpointy musia vracať 404. Host/Origin/CSRF podvrhnutie musí zlyhať. Link vyžadujúci DSM účet sa odmieta bez prihlasovacej stránky.
9. Overte limity a buffering DSM Reverse Proxy/Web Station; UI nemôže potvrdiť, že sa celý upload nespúluje už pred proxy kontajnerom.
10. Skontrolujte logy reverznej proxy a DSM: share tokeny, heslá, cookies a interné cesty sa nemajú dostať do verejných/centralizovaných logov. Overte, že `.har`, `.env` a certifikáty nie sú v Git histórii.

Pri nezodpovedajúcej odpovedi doplňte iba konkrétny fix uzavretého protokolu a test. Nezapínajte všeobecnú reverse proxy, DSM credentials ani vypnutie TLS. HAR odosielajte súkromne po anonymizácii; browser „sanitized export“ nemusí odstrániť všetky bearer tokeny v URL/telách.
