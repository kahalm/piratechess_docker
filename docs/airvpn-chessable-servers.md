# AirVPN-Server für Chessable (funktionierend)
> Stand **2026-06-30**. Welche AirVPN-Exit-IPs Chessables Cloudflare **durchlässt** vs. **blockt (HTTP 403)**.
## Methode
Entscheidend ist die **ASN der Exit-IP**, nicht das Land. Geprüft via `POST /api/chessable/direct/test` (piratechess, Tunnel gepinnt) mit einem gültigen Chessable-Bearer — je ein Server pro distinkter ASN live getestet. Klassifizierung aller 255 Server per `ip_v4_in1`-/24 → ASN (ip-api.com).
## Blockierte ASNs (HTTP 403 — NICHT nutzen)
- **AS9009 — M247 Europe SRL**
- **AS212238 — Datacamp Limited** (auch PIA läuft hierüber → PIA unbrauchbar)
- **AS206804 — EstNOC OU**
Alle anderen getesteten ASNs funktionieren.
## Übersicht

**178 von 255 Servern nutzbar.**

| Land | nutzbar | blockiert |
|------|--------:|----------:|
| Netherlands | 74 | 0 |
| United States | 42 | 6 |
| Canada | 36 | 2 |
| Sweden | 13 | 0 |
| Latvia | 4 | 0 |
| New Zealand | 4 | 0 |
| Germany | 3 | 11 |
| Romania | 1 | 2 |
| Taiwan | 1 | 0 |
| Serbia | 0 | 2 |
| Brazil | 0 | 1 |
| Czech Republic | 0 | 3 |
| Japan | 0 | 8 |
| Spain | 0 | 4 |
| Estonia | 0 | 1 |
| United Kingdom | 0 | 6 |
| Belgium | 0 | 5 |
| Norway | 0 | 5 |
| Austria | 0 | 3 |
| Switzerland | 0 | 8 |
| Singapore | 0 | 7 |
| Ireland | 0 | 1 |
| Bulgaria | 0 | 2 |

## gluetun-Nutzung
In `compose.yaml`/`.env` des VPN-Tunnels: `SERVER_COUNTRIES=<Land>` + `SERVER_NAMES=<Name1,Name2,...>` (Namen aus den Tabellen unten). Niedrige Latenz (EU): **Netherlands** ist der größte saubere Pool; DE-Pin = `Adhil,Ashlesha,Fuyue` (Netrouting).

## Funktionierende Server pro Land

### Netherlands (74)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Alchiba | Alblasserdam | 213.152.161.180 | AS49453 Global Layer B.V. |
| Alcyone | Alblasserdam | 213.152.161.116 | AS49453 Global Layer B.V. |
| Aljanah | Alblasserdam | 134.19.179.170 | AS49453 Global Layer B.V. |
| Alphard | Alblasserdam | 213.152.187.199 | AS49453 Global Layer B.V. |
| Alphecca | Alblasserdam | 213.152.187.194 | AS49453 Global Layer B.V. |
| Alpheratz | Alblasserdam | 134.19.179.242 | AS49453 Global Layer B.V. |
| Alphirk | Alblasserdam | 213.152.187.214 | AS49453 Global Layer B.V. |
| Alrai | Alblasserdam | 213.152.162.78 | AS49453 Global Layer B.V. |
| Alshat | Alblasserdam | 213.152.161.4 | AS49453 Global Layer B.V. |
| Alterf | Alblasserdam | 213.152.161.169 | AS49453 Global Layer B.V. |
| Alzirr | Alblasserdam | 213.152.187.204 | AS49453 Global Layer B.V. |
| Ancha | Alblasserdam | 213.152.162.164 | AS49453 Global Layer B.V. |
| Andromeda | Alblasserdam | 213.152.161.228 | AS49453 Global Layer B.V. |
| Anser | Alblasserdam | 213.152.186.18 | AS49453 Global Layer B.V. |
| Asellus | Alblasserdam | 213.152.187.209 | AS49453 Global Layer B.V. |
| Aspidiske | Alblasserdam | 134.19.179.194 | AS49453 Global Layer B.V. |
| Atik | Alblasserdam | 213.152.161.9 | AS49453 Global Layer B.V. |
| Canis | Alblasserdam | 213.152.161.218 | AS49453 Global Layer B.V. |
| Capella | Alblasserdam | 134.19.179.138 | AS49453 Global Layer B.V. |
| Caph | Alblasserdam | 213.152.162.169 | AS49453 Global Layer B.V. |
| Celaeno | Alblasserdam | 213.152.161.68 | AS49453 Global Layer B.V. |
| Chara | Alblasserdam | 213.152.187.219 | AS49453 Global Layer B.V. |
| Comae | Alblasserdam | 213.152.186.162 | AS49453 Global Layer B.V. |
| Crater | Alblasserdam | 213.152.162.14 | AS49453 Global Layer B.V. |
| Cygnus | Alblasserdam | 213.152.161.243 | AS49453 Global Layer B.V. |
| Dalim | Alblasserdam | 134.19.179.210 | AS49453 Global Layer B.V. |
| Diphda | Alblasserdam | 213.152.161.164 | AS49453 Global Layer B.V. |
| Edasich | Alblasserdam | 213.152.161.210 | AS49453 Global Layer B.V. |
| Elnath | Alblasserdam | 213.152.186.39 | AS49453 Global Layer B.V. |
| Eltanin | Alblasserdam | 134.19.179.146 | AS49453 Global Layer B.V. |
| Garnet | Alblasserdam | 213.152.162.73 | AS49453 Global Layer B.V. |
| Gianfar | Alblasserdam | 213.152.161.100 | AS49453 Global Layer B.V. |
| Gienah | Alblasserdam | 213.152.162.93 | AS49453 Global Layer B.V. |
| Hassaleh | Alblasserdam | 213.152.161.39 | AS49453 Global Layer B.V. |
| Horologium | Alblasserdam | 213.152.162.4 | AS49453 Global Layer B.V. |
| Hyadum | Alblasserdam | 213.152.161.34 | AS49453 Global Layer B.V. |
| Hydrus | Alblasserdam | 213.152.162.9 | AS49453 Global Layer B.V. |
| Jabbah | Alblasserdam | 213.152.186.23 | AS49453 Global Layer B.V. |
| Kajam | Alblasserdam | 213.152.161.84 | AS49453 Global Layer B.V. |
| Kocab | Alblasserdam | 213.152.162.180 | AS49453 Global Layer B.V. |
| Larawag | Alblasserdam | 134.19.179.178 | AS49453 Global Layer B.V. |
| Luhman | Alblasserdam | 213.152.186.167 | AS49453 Global Layer B.V. |
| Maasym | Alblasserdam | 213.152.162.103 | AS49453 Global Layer B.V. |
| Matar | Alblasserdam | 213.152.187.224 | AS49453 Global Layer B.V. |
| Melnick | Alblasserdam | 134.19.179.162 | AS49453 Global Layer B.V. |
| Menkent | Alblasserdam | 213.152.176.134 | AS49453 Global Layer B.V. |
| Merga | Alblasserdam | 213.152.161.29 | AS49453 Global Layer B.V. |
| Mirach | Alblasserdam | 213.152.162.68 | AS49453 Global Layer B.V. |
| Miram | Alblasserdam | 213.152.162.88 | AS49453 Global Layer B.V. |
| Muhlifain | Alblasserdam | 134.19.179.202 | AS49453 Global Layer B.V. |
| Muscida | Alblasserdam | 213.152.162.153 | AS49453 Global Layer B.V. |
| Musica | Alblasserdam | 213.152.161.248 | AS49453 Global Layer B.V. |
| Nash | Alblasserdam | 213.152.161.24 | AS49453 Global Layer B.V. |
| Orion | Alblasserdam | 213.152.161.238 | AS49453 Global Layer B.V. |
| Phaet | Alblasserdam | 213.152.187.229 | AS49453 Global Layer B.V. |
| Piautos | Alblasserdam | 134.19.178.166 | AS49453 Global Layer B.V. |
| Piscium | Alblasserdam | 134.19.179.130 | AS49453 Global Layer B.V. |
| Pleione | Alblasserdam | 213.152.162.148 | AS49453 Global Layer B.V. |
| Pyxis | Alblasserdam | 213.152.161.233 | AS49453 Global Layer B.V. |
| Rukbat | Alblasserdam | 213.152.162.83 | AS49453 Global Layer B.V. |
| Sadr | Alblasserdam | 213.152.187.234 | AS49453 Global Layer B.V. |
| Salm | Alblasserdam | 213.152.161.19 | AS49453 Global Layer B.V. |
| Scuti | Alblasserdam | 134.19.179.154 | AS49453 Global Layer B.V. |
| Sheliak | Alblasserdam | 213.152.186.34 | AS49453 Global Layer B.V. |
| Situla | Alblasserdam | 213.152.161.14 | AS49453 Global Layer B.V. |
| Subra | Alblasserdam | 213.152.162.98 | AS49453 Global Layer B.V. |
| Suhail | Alblasserdam | 134.19.179.186 | AS49453 Global Layer B.V. |
| Taiyangshou | Amsterdam | 94.228.209.178 | AS6206 Netrouting B.V. |
| Talitha | Alblasserdam | 213.152.161.137 | AS49453 Global Layer B.V. |
| Tarazed | Alblasserdam | 213.152.161.132 | AS49453 Global Layer B.V. |
| Tiaki | Alblasserdam | 134.19.179.234 | AS49453 Global Layer B.V. |
| Tianyi | Alblasserdam | 213.152.186.172 | AS49453 Global Layer B.V. |
| Vindemiatrix | Amsterdam | 94.228.209.210 | AS6206 Netrouting B.V. |
| Zibal | Alblasserdam | 213.152.161.148 | AS49453 Global Layer B.V. |

### United States (42)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Aquila | Fremont, California | 23.130.104.129 | AS62744 Quintex Alliance Consulting |
| Bunda | San Jose, California | 198.54.134.251 | AS11878 tzulo, inc. |
| Chamaeleon | Dallas, Texas | 204.8.98.10 | AS62744 Quintex Alliance Consulting |
| Dziban | Miami | 45.92.16.130 | AS6206 Netrouting B.V. |
| Equuleus | Dallas, Texas | 204.8.98.20 | AS62744 Quintex Alliance Consulting |
| Fang | Chicago, Illinois | 68.235.48.107 | AS11878 tzulo, inc. |
| Guniibuu | Phoenix, Arizona | 198.44.133.67 | AS11878 tzulo, inc. |
| Helvetios | Dallas, Texas | 204.8.98.30 | AS62744 Quintex Alliance Consulting |
| Hercules | Atlanta, Georgia | 64.42.179.58 | AS63018 Dedicated.com |
| Imai | San Jose, California | 198.44.134.3 | AS11878 tzulo, inc. |
| Khambalia | Phoenix, Arizona | 198.44.133.83 | AS11878 tzulo, inc. |
| Kruger | Chicago, Illinois | 68.235.35.123 | AS11878 tzulo, inc. |
| Leo | Dallas, Texas | 204.8.98.40 | AS62744 Quintex Alliance Consulting |
| Libra | Atlanta, Georgia | 64.42.179.66 | AS63018 Dedicated.com |
| Maia | Los Angeles | 198.54.129.51 | AS11878 tzulo, inc. |
| Mensa | Dallas, Texas | 204.8.98.50 | AS62744 Quintex Alliance Consulting |
| Meridiana | Chicago, Illinois | 68.235.35.251 | AS11878 tzulo, inc. |
| Muliphein | New York City | 198.44.136.235 | AS11878 tzulo, inc. |
| Musca | Atlanta, Georgia | 64.42.179.42 | AS63018 Dedicated.com |
| Paikauhale | New York City | 198.44.136.251 | AS11878 tzulo, inc. |
| Pegasus | Dallas, Texas | 204.8.98.60 | AS62744 Quintex Alliance Consulting |
| Polis | Raleigh, North Carolina | 198.54.130.27 | AS11878 tzulo, inc. |
| Praecipua | Chicago, Illinois | 68.235.52.67 | AS11878 tzulo, inc. |
| Ran | Dallas, Texas | 204.8.98.70 | AS62744 Quintex Alliance Consulting |
| Revati | Los Angeles | 198.54.129.123 | AS11878 tzulo, inc. |
| Sadachbia | Denver, Colorado | 198.54.128.123 | AS11878 tzulo, inc. |
| Sadalmelik | New York City | 198.44.159.3 | AS11878 tzulo, inc. |
| Sadalsuud | Chicago, Illinois | 68.235.35.179 | AS11878 tzulo, inc. |
| Sarin | Los Angeles | 198.54.129.59 | AS11878 tzulo, inc. |
| Sculptor | Atlanta, Georgia | 64.42.179.34 | AS63018 Dedicated.com |
| Scutum | Dallas, Texas | 204.8.98.80 | AS62744 Quintex Alliance Consulting |
| Sheratan | Phoenix, Arizona | 198.44.133.75 | AS11878 tzulo, inc. |
| Sneden | Chicago, Illinois | 68.235.52.35 | AS11878 tzulo, inc. |
| Superba | Chicago, Illinois | 208.77.22.211 | AS11878 tzulo, inc. |
| Terebellum | New York City | 198.44.136.27 | AS11878 tzulo, inc. |
| Torcular | Denver, Colorado | 198.54.128.115 | AS11878 tzulo, inc. |
| Unukalhai | New York City | 198.44.136.243 | AS11878 tzulo, inc. |
| Unurgunite | New York City | 198.44.159.11 | AS11878 tzulo, inc. |
| Ursa | Atlanta, Georgia | 64.42.179.50 | AS63018 Dedicated.com |
| Volans | Dallas, Texas | 204.8.98.90 | AS62744 Quintex Alliance Consulting |
| Vulpecula | Dallas, Texas | 204.8.98.100 | AS62744 Quintex Alliance Consulting |
| Xamidimura | Los Angeles | 198.54.129.43 | AS11878 tzulo, inc. |

### Canada (36)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Agena | Toronto, Ontario | 184.75.223.210 | AS32489 Amanah Tech Inc. |
| Alhena | Toronto, Ontario | 162.219.176.2 | AS32489 Amanah Tech Inc. |
| Alkurhah | Toronto, Ontario | 184.75.221.202 | AS32489 Amanah Tech Inc. |
| Aludra | Toronto, Ontario | 104.254.90.202 | ? unbekannt |
| Alwaid | Toronto, Ontario | 184.75.221.106 | AS32489 Amanah Tech Inc. |
| Alya | Toronto, Ontario | 184.75.221.170 | AS32489 Amanah Tech Inc. |
| Angetenar | Toronto, Ontario | 184.75.221.162 | AS32489 Amanah Tech Inc. |
| Arkab | Toronto, Ontario | 184.75.221.210 | AS32489 Amanah Tech Inc. |
| Avior | Toronto, Ontario | 184.75.223.234 | AS32489 Amanah Tech Inc. |
| Castula | Toronto, Ontario | 198.44.157.51 | AS11878 tzulo, inc. |
| Cephei | Toronto, Ontario | 184.75.214.162 | AS32489 Amanah Tech Inc. |
| Chamukuy | Toronto, Ontario | 198.44.157.131 | AS11878 tzulo, inc. |
| Chort | Toronto, Ontario | 104.254.90.234 | ? unbekannt |
| Elgafar | Toronto, Ontario | 198.44.157.59 | AS11878 tzulo, inc. |
| Enif | Toronto, Ontario | 104.254.90.242 | ? unbekannt |
| Ginan | Vancouver | 104.193.135.242 | AS394256 Tech Futures Interactive Inc. |
| Gorgonea | Toronto, Ontario | 104.254.90.250 | ? unbekannt |
| Kornephoros | Toronto, Ontario | 198.44.157.11 | AS11878 tzulo, inc. |
| Lesath | Toronto, Ontario | 184.75.221.2 | AS32489 Amanah Tech Inc. |
| Mintaka | Toronto, Ontario | 184.75.223.218 | AS32489 Amanah Tech Inc. |
| Nahn | Vancouver | 192.30.89.66 | AS394256 Tech Futures Interactive Inc. |
| Pisces | Vancouver | 192.30.89.26 | AS394256 Tech Futures Interactive Inc. |
| Regulus | Toronto, Ontario | 184.75.221.34 | AS32489 Amanah Tech Inc. |
| Rotanev | Toronto, Ontario | 104.254.90.186 | ? unbekannt |
| Sadalbari | Toronto, Ontario | 184.75.221.178 | AS32489 Amanah Tech Inc. |
| Saiph | Toronto, Ontario | 184.75.223.226 | AS32489 Amanah Tech Inc. |
| Sargas | Toronto, Ontario | 184.75.223.194 | AS32489 Amanah Tech Inc. |
| Sham | Vancouver | 192.30.89.74 | AS394256 Tech Futures Interactive Inc. |
| Sharatan | Toronto, Ontario | 104.254.90.194 | ? unbekannt |
| Sualocin | Toronto, Ontario | 184.75.221.42 | AS32489 Amanah Tech Inc. |
| Tegmen | Toronto, Ontario | 184.75.208.242 | AS32489 Amanah Tech Inc. |
| Tejat | Toronto, Ontario | 184.75.221.194 | AS32489 Amanah Tech Inc. |
| Telescopium | Vancouver | 192.30.89.50 | AS394256 Tech Futures Interactive Inc. |
| Titawin | Vancouver | 192.30.89.58 | AS394256 Tech Futures Interactive Inc. |
| Tyl | Toronto, Ontario | 184.75.223.202 | AS32489 Amanah Tech Inc. |
| Ukdah | Toronto, Ontario | 184.75.221.58 | AS32489 Amanah Tech Inc. |

### Sweden (13)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Albali | Uppsala | 62.102.148.149 | AS51815 GlobalConnect AB |
| Algorab | Uppsala | 62.102.148.147 | AS51815 GlobalConnect AB |
| Alrami | Uppsala | 62.102.148.145 | AS51815 GlobalConnect AB |
| Alula | Uppsala | 62.102.148.151 | AS51815 GlobalConnect AB |
| Atria | Uppsala | 62.102.148.150 | AS51815 GlobalConnect AB |
| Azmidiske | Uppsala | 62.102.148.141 | AS51815 GlobalConnect AB |
| Benetnasch | Uppsala | 62.102.148.148 | AS51815 GlobalConnect AB |
| Copernicus | Stockholm | 79.142.76.243 | AS51430 AltusHost B.V. |
| Lupus | Stockholm | 128.127.105.183 | AS51430 AltusHost B.V. |
| Menkab | Uppsala | 62.102.148.143 | AS51815 GlobalConnect AB |
| Muphrid | Uppsala | 62.102.148.146 | AS51815 GlobalConnect AB |
| Norma | Stockholm | 31.3.152.99 | AS51430 AltusHost B.V. |
| Segin | Stockholm | 94.185.80.226 | AS6206 Netrouting B.V. |

### New Zealand (4)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Fawaris | Auckland | 103.231.91.58 | AS133480 5G NETWORK OPERATIONS PTY LTD |
| Mothallah | Auckland | 223.165.69.100 | AS45179 SiteHost New Zealand |
| Theemin | Auckland | 202.50.176.4 | AS45179 SiteHost New Zealand |
| Tianguan | Auckland | 223.165.69.68 | AS45179 SiteHost New Zealand |

### Latvia (4)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Felis | Riga | 46.183.217.98 | AS52048 SIA RixHost |
| Meissa | Riga | 109.248.148.242 | AS52048 SIA RixHost |
| Phact | Riga | 46.183.218.146 | AS52048 SIA RixHost |
| Schedir | Riga | 84.38.135.2 | AS52048 SIA RixHost |

### Germany (3)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Adhil | Frankfurt | 37.46.199.66 | AS6206 Netrouting B.V. |
| Ashlesha | Frankfurt | 37.46.199.50 | AS6206 Netrouting B.V. |
| Fuyue | Frankfurt | 37.46.199.82 | AS6206 Netrouting B.V. |

### Romania (1)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Nembus | Bucharest | 37.46.196.18 | AS6206 Netrouting B.V. |

### Taiwan (1)

| Server | Standort | Entry-IP (ip_v4_in1) | ASN |
|--------|----------|----------------------|-----|
| Sulafat | Taipei | 103.230.144.100 | AS55720 Gigabit Hosting Sdn Bhd |
