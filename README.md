# Synology Public Share Proxy

Samostatné verejné rozhranie pre **File Station download odkazy a anonymné File Request uploady**. ASP.NET Core 10 Minimal API + čisté HTML/CSS/JavaScript. Zdieľanie, súbory, heslá a oprávnenia zostávajú v Synology. Proxy nepotrebuje DSM účet, API kľúč ani pripojenie súborových zväzkov.

Projekt je implementovaný podľa používateľových DSM 7.3 HAR záznamov. **Testy a lokálne UI QA prešli; tento build zatiaľ nebol spustený proti fyzickému DSM.** Pred verejným nasadením vykonajte [overenie na DSM](docs/DSM-VALIDATION.md). Docker build je súčasťou CI; lokálne nebol dostupný Docker daemon.

## Čo funguje

- Zoznam súborov, prechádzanie priečinkov, stránkovanie, hľadanie v načítaných položkách a zoradenie názvov.
- Výber súborov/priečinkov a download cez natívny Synology ZIP stream; samostatný súbor cez natívny `fsdownload`.
- Verejné zdieľanie s heslom: zachytené Synology RSA/AES šifrovanie, izolované cookies a relácia iba pre príslušný odkaz.
- File Request: výber alebo drag-and-drop, fronta, progres, zrušenie, opakovanie a potvrdenie uloženia od DSM.
- Mobilné rozhranie, popisy ovládacích prvkov, klávesnicový výber, živé stavy, viditeľné zameranie a obmedzená animácia.
- Chybový/expiračný stav. Platnosť sa znovu overuje pred každým zoznamom, downloadom a uploadom.
- Streamovanie bez ukladania kópií na disk proxy; pevný DSM origin, TLS validácia, limity veľkosti/času/súbehu, rate limiting, CSRF a bezpečnostné hlavičky.

Rozhranie je navrhnuté v duchu File Station. Nepoužíva Synology JavaScript, CSS, fonty, obrázky, logo ani iné proprietárne assety.

## Hranice podpory

Podporované sú verejné odkazy bez hesla alebo s heslom. Odkazy vyžadujúce DSM účet, interné používateľské prihlásenie, IP binding, limit počtu návštev, redirect typ a ďalšie aplikácie (Drive, Photos, administrácia) sa odmietajú. IP binding a počítadlá by cez proxy menili svoju sémantiku. Časovú expiráciu a prístupové oprávnenia naďalej vynucuje DSM.

Naraz možno stiahnuť najviac 100 vybraných položiek. Veľký vnorený priečinok možno vybrať ako jednu položku z jeho nadradeného priečinka. Tlačidlo „Stiahnuť všetko“ vyžaduje načítanie všetkých položiek aktuálneho priečinka. Na jeden odkaz v relácii je limit 4 096 evidovaných položiek; nová relácia vyprázdni evidenciu. Konzervatívna validácia odmieta problematické názvy/cesty vrátane riadiacich znakov, lomiek, `%` v cestách a segmentov `.`/`..`.

Upload nikdy úmyselne neprepisuje existujúce súbory (`overwrite=false`); konflikt rieši DSM. Po zrušení prenosu môže DSM mať čiastočný alebo už dokončený súbor. Proxy ho nemaže a neskúša zrušenie automaticky opakovať. Fronta existuje iba v pamäti otvorenej stránky.

## Rýchle nasadenie na Synology

Vyžaduje Synology s Container Manager a podporovaným `linux/amd64` alebo `linux/arm64`. Image používa [.NET 10 runtime](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md). Pre konkrétny NAS model je potrebné potvrdiť podporu architektúry a kontajnerov.

1. Uložte zdrojový priečinok do napr. `/volume1/docker/synology-public-share-proxy`.
2. Skopírujte `.env.example` ako `.env` a nastavte:
   - `DSM_ORIGIN`: interný HTTPS origin DSM dostupný **z kontajnera**, napr. `https://nas.home.example:5001`. Certifikát musí mať zodpovedajúci hostname a dôveryhodný reťazec. Táto adresa nesmie smerovať späť na proxy.
   - `PUBLIC_ORIGIN`: presný verejný HTTPS origin, napr. `https://share.example.com`, bez cesty.
3. Ak používate internú CA, vložte iba jej verejný `.crt` certifikát do `deploy/ca/` pred buildom. Dockerfile ho zaradí medzi dôveryhodné CA. Súkromný kľúč sa nepoužíva. Overovanie TLS nemožno vypnúť.
4. V Container Manager → Project vytvorte projekt z tohto adresára a súboru `compose.yaml`; spustite build a kontajner. Ekvivalent cez terminál:

   ```sh
   docker compose up -d --build
   docker compose ps
   ```

5. V DSM → Control Panel → Login Portal → Advanced → Reverse Proxy nastavte zdroj `HTTPS`, `share.example.com`, port `443`; cieľ `HTTP`, `127.0.0.1`, port `8088`.
6. Priraďte verejný certifikát k tejto doméne. Zachovajte HTTP `Host: share.example.com` a `Origin`. Proxy host kontroluje; nesprávny Host dá `400`.
7. Limit request body reverse proxy nastavte aspoň na `MAX_UPLOAD_BYTES` a timeouty na `TRANSFER_TIMEOUT_SECONDS`. Vypnite upload/download buffering, ak to vaša spravovaná konfigurácia podporuje. [Ukážka nginx](deploy/nginx.example.conf) je určená pre nginx, ktorý spravujete; neupravujte systémové DSM súbory, ktoré DSM prepisuje.
8. Verejná doména musí smerovať iba na túto aplikáciu. DSM porty `5000/5001`, Drive a administráciu neotvárajte cez túto doménu. Proxy kontajner nevyžaduje privilegovaný režim ani volume so súbormi NAS.

Web Station môže poslúžiť ako frontend reverznej proxy pre tento kontajner. C# aplikácia beží v kontajneri na porte 8080; konfigurácia nie je PHP webroot. HTTPS verejnej stránky a HTTPS upstream DSM sú dve oddelené spojenia. Na HTTP cieľ reverse proxy neposielajte HTTPS a na HTTPS port DSM neposielajte HTTP.

Compose zverejňuje port iba na host loopback; pri reverznej proxy na inom stroji ho vystavte iba na dôveryhodnej internej adrese a obmedzte firewallom. Reverse proxy má byť jediná verejná cesta. Štandardný per-IP limiter vidí IP reverznej proxy: `X-Forwarded-For` sa nepreberá, aby ho návštevník nemohol podvrhnúť. Pre veľký počet návštevníkov pridajte per-client rate limiting na dôveryhodnú reverznú proxy; neodstraňujte globálny limiter.

## Ako poslať verejný odkaz

Vytvorte share alebo File Request v DSM File Station ako doteraz. Zachovajte token a cestu, zmeňte iba verejný host:

```text
Pôvodné: https://nas.home.example:5001/sharing/EXAMPLE123
Verejné: https://share.example.com/sharing/EXAMPLE123
```

`EXAMPLE123` je vymyslený príklad. Funguje aj `/s/<token>`. Návštevník nedostáva DSM HTML, admin/login stránku, upstream URL, DSM cookie ani filesystem cestu. Krátke `gofile.me` redirecty sa nepreberajú; použite priamy File Station odkaz. Konfigurácia external domain a automatické generovanie proxy linkov sa v DSM nemení programovo.

## Limity a nastavenia

| Nastavenie | Predvolené |
| --- | --- |
| `Proxy__MaxUploadBytes` | 1 GiB na súbor |
| `Proxy__MaxDownloadBytes` | 10 GiB na stream |
| `Proxy__MaxConcurrentTransfers` | 4 pre celý proces |
| `Proxy__TransferTimeoutSeconds` | 1 800 |
| `Proxy__MetadataTimeoutSeconds` | 20 |
| `Proxy__IdleTimeoutSeconds` | 60 |
| `Proxy__SessionMinutes` | 30, absolútna platnosť |
| `Proxy__MaxSessions` | 256 návštevníkov |
| `Proxy__UploadsEnabled` | true |
| `Proxy__PasswordLoginEnabled` | true |

Jedna relácia otvorí najviac 8 share tokenov, každý má oddelený HTTP klient/cookie store. Celkový počet spojení k DSM je obmedzený na `MaxSessions` (predvolene 256). Identifikátory súborov majú limit 4 096 na odkaz a 32 768 za celý proces. Súbežné operácie na tom istom odkaze vrátia `429`, ktoré UI vysvetlí; upload fronta posiela súbory postupne. Restart ruší relácie a návštevníci zadajú heslo znova. Pri neznámej dĺžke downloadu sa limit kontroluje počas čítania; už začatý príliš veľký alebo chybný stream sa preruší. Download sa neukladá do pamäte JavaScriptu. Resume/range, preview a thumbnail API sú zatiaľ mimo rozsahu.

## Vývoj, testy a lokálny náhľad

```sh
dotnet test SynologyPublicShareProxy.slnx -c Release
node --check src/ShareProxy/wwwroot/app.js
python tests/http_smoke.py
```

`http_smoke.py` používa Release build vytvorený testami a spustí izolovaný localhost proces. Testy majú iba syntetické dáta; HAR ani tajomstvá nie sú súčasťou repozitára.

Lokálny UI náhľad v PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Proxy__DemoMode='true'
$env:Proxy__PublicOrigin='http://localhost:5188'
dotnet run --project src/ShareProxy --urls http://localhost:5188
```

Otvorte `http://localhost:5188/s/demo-share`, `/s/demo-upload`, `/s/demo-password` (heslo `atelier`) alebo `/s/demo-expired`. Ukážkové uploady sa zahadzujú. Demo režim vyžaduje Development a loopback návštevníka; produkčný štart s DemoMode zlyhá. V produkcii nechajte `Proxy__DemoMode=false` a `ASPNETCORE_ENVIRONMENT=Production`.

Záznam výsledkov a screenshoty: [QA](docs/QA.md). Presný zoznam zachytených operácií a hraníc poznania: [protokol](docs/PROTOCOL.md). Verejné API: [API](docs/API.md).

## Bezpečnosť a prevádzkové logy

Share token je bearer secret. Aplikácia neloguje URL, názvy súborov, telá požiadaviek, heslá, upstream hlavičky ani výnimky s ich obsahom. Zabezpečte rovnaké pravidlo na DSM/Web Station/reverse proxy a prípadnom CDN: URL token sa môže objaviť v ich access/error logoch. Ukážka nginx vypína logovanie tohto virtual hosta. HAR nepoužívajte ako verejné issue prílohy.

Proxy nemá žiadny endpoint na všeobecné forwardovanie. Neberie upstream URL, API meno ani raw filesystem path od návštevníka. Náhodné file ID platí iba pre položku enumerovanú v danej zdieľacej relácii; pred downloadom sa znovu overí dostupnosť share. Všetky ďalšie cesty vracajú 404. Cookies sú HttpOnly, SameSite=Strict a v produkcii Secure s `__Host-` prefixom. Origin, Host a CSRF sa kontrolujú pri zápisoch; CSP zakazuje cudzie skripty a framing.

DSM origin a jeho DNS sú dôveryhodná konfigurácia prevádzkovateľa. Obmedzte egress kontajnera na NAS a dôveryhodné DNS; návštevník nemôže adresu zmeniť. TLS reťazec a hostname sa overujú štandardným .NET handlerom; redirecty, systémová HTTP proxy aj ambientné credentials sú vypnuté. Žiadne DSM admin credentials nepatria do `.env`.

## Inšpirácia

[immich-public-proxy](https://github.com/alangrainger/immich-public-proxy) poslúžil ako referencia pre princíp verejného zdieľania bez privilegovaného API kľúča, overovanie členstva súboru v zdieľaní a odmietnutie ostatných trás. Preštudované boli jeho README, `app/src/index.ts` a `app/src/immich.ts`. Tento projekt má samostatnú implementáciu; kód ani assety referenčného projektu neboli skopírované.
