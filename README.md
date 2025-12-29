
# Firebird Metadata Tool (.NET 8)

Konsolowa aplikacja w .NET 8.0 do:
- **exportu** metadanych bazy Firebird 5.0 do plików `.sql`,
- **utworzenia** nowej bazy na podstawie skryptów,
- **aktualizacji** istniejącej bazy na podstawie skryptów.

Obsługiwane elementy:
- **Domains**
- **Tables (columns)**
- **Procedures**

## Wymagania
- .NET SDK 8.0
- Firebird 5.0 (serwer lokalny lub zdalny)
- Dostęp do bazy / pliku `.fdb`

## Struktura katalogu skryptów

Aplikacja oczekuje katalogu ze skryptami w strukturze:

```

scripts/
  domains/
    *.sql
  tables/
    *.sql
  procedures/
    *.sql

```

Pliki w każdym folderze są wykonywane **alfabetycznie**, więc warto stosować nazwy z prefiksami (np. `001_users.sql`, `002_books.sql`), jeśli kolejność ma znaczenie.

Każdy plik powinien zawierać **jedno polecenie SQL** (np. `CREATE DOMAIN ...`, `CREATE TABLE ...`, `CREATE OR ALTER PROCEDURE ...`).
Przed wykonaniem narzędzie usuwa BOM, `SET TERM`, komentarze liniowe/blokowe oraz końcowy średnik, dlatego pojedyncze polecenie na plik upraszcza i ujednolica wykonanie.

## Komendy

### 1) Export skryptów z istniejącej bazy

Generuje pliki `.sql` w strukturze `domains/tables/procedures`.

```bash
dotnet run export-scripts \
  --connection-string "database=localhost/3050:C:\path\to\TEST.FDB;user=SYSDBA;password=<YOUR_PASSWORD>" \
  --output-dir ".\out"
```

Efekt:

```
out/
  domains/
  tables/
  procedures/
```

Każdy obiekt jest eksportowany do osobnego pliku (1 plik = 1 obiekt).

### 2) Build - utworzenie nowej bazy na podstawie skryptów

Tworzy pustą bazę w `--db-dir` i wykonuje skrypty z `--scripts-dir`.

```bash
dotnet run build-db \
  --db-dir ".\data" \
  --scripts-dir ".\src\Examples\Library"
```

Domyślnie baza tworzy plik:

* `database.fdb` w `--db-dir`

Procedury są wykonywane w **dwóch transakcjach**:
1. Stub nagłówka (`CREATE OR ALTER PROCEDURE ... AS BEGIN END`) - aby umożliwić kompilację zależnych obiektów.
2. Docelowe body - właściwa treść procedury.

Jeśli w drugiej transakcji wystąpi błąd body, nowa baza zostanie utworzona z pustym stubem procedury. Popraw skrypt i ponów `build-db`, aby baza została odtworzona z pełnymi procedurami.

#### Konfiguracja logowania dla build-db

Build używa zmiennych środowiskowych:

* `FB_USER` - użytkownik bazy
* `FB_PASSWORD` - hasło użytkownika
* `FB_HOST` - host serwera
* `DB_FILE_NAME` - opcjonalna nazwa pliku bazy (domyślnie `database.fdb`)

Bash:

```bash
export FB_USER="SYSDBA"
export FB_PASSWORD="<YOUR_PASSWORD>"
export FB_HOST="localhost"
export DB_FILE_NAME="database.fdb" # opcjonalnie
```

PowerShell:

```powershell
$env:FB_USER="SYSDBA"
$env:FB_PASSWORD="<YOUR_PASSWORD>"
$env:FB_HOST="localhost"
$env:DB_FILE_NAME="database.fdb" # opcjonalnie
```

### 3) Update - aktualizacja istniejącej bazy na podstawie skryptów

Wykonuje skrypty w kolejności: `domains → tables → procedures`.

```bash
dotnet run update-db \
  --connection-string "database=localhost/3050:C:\path\to\DATABASE.FDB;user=SYSDBA;password=<YOUR_PASSWORD>" \
  --scripts-dir ".\src\Examples\Library"
```

Procedury są wykonywane w **dwóch fazach**:
1. Stub nagłówka (`CREATE OR ALTER PROCEDURE ... AS BEGIN END`) - aby umożliwić kompilację zależnych obiektów.
2. Docelowe body - właściwa treść procedury.

Jeśli w drugiej fazie wystąpi błąd body, w bazie pozostanie stub (puste body). Popraw skrypt procedury i uruchom `update-db` ponownie; w razie potrzeby przywróć poprzednią wersję procedury ręcznie przed ponowną próbą.

### Zmienne środowiskowe Update

* `FB_DRY_RUN=1` - tylko plan (bez SQL), z listą blokad zależności i kandydatów do DROP.
* `FB_DESTRUCTIVE=1` - pozwala na DROP brakujących obiektów/kolumn (z kontrolą zależności). Domyślnie DROP-y są tylko raportowane.
* `FB_DEBUG_COMPARE=1` - wypisuje szczegóły różnic (typ/default/null/validation) dla domen i kolumn.
* `FB_RECHECK=1` - po ALTER domen/kolumn ponownie czyta metadane i raportuje, jeśli różnice nie zniknęły.

Przykłady:

```bash
FB_DRY_RUN=1 dotnet run update-db --connection-string "<...>" --scripts-dir "./scripts"
FB_DESTRUCTIVE=1 dotnet run update-db --connection-string "<...>" --scripts-dir "./scripts"
FB_DRY_RUN=1 FB_DESTRUCTIVE=1 dotnet run update-db --connection-string "<...>" --scripts-dir "./scripts"
```

PowerShell:

```powershell
$env:FB_DRY_RUN=1; dotnet run update-db --connection-string "<...>" --scripts-dir ".\scripts"
$env:FB_DESTRUCTIVE=1; dotnet run update-db --connection-string "<...>" --scripts-dir ".\scripts"
$env:FB_DRY_RUN=1; $env:FB_DESTRUCTIVE=1; dotnet run update-db --connection-string "<...>" --scripts-dir ".\scripts"
$env:FB_DEBUG_COMPARE=1; $env:FB_RECHECK=1; dotnet run update-db --connection-string "<...>" --scripts-dir ".\scripts"
```

### Szybki start

Podstawowe wywołania (kody wyjścia: `0` sukces, `1` brak/nieznane polecenie, `-1` błąd wykonania):

```bash
# Budowa nowej bazy na podstawie skryptów
dotnet run build-db --db-dir "./data" --scripts-dir "./scripts"

# Eksport metadanych z istniejącej bazy
dotnet run export-scripts --connection-string "<connection_string>" --output-dir "./out"

# Aktualizacja istniejącej bazy na podstawie skryptów
dotnet run update-db --connection-string "<connection_string>" --scripts-dir "./scripts"
```

## Jak działa update (zasady)

* **Domains**

  * `CREATE DOMAIN` istniejącej domeny → `ALTER DOMAIN` (TYPE/DEFAULT/NULL/VALIDATION) w jednej transakcji.
  * DROP domeny tylko przy `FB_DESTRUCTIVE=1` i braku zależności.

* **Tables**

  * brakująca tabela → `CREATE TABLE ...`
  * istniejąca tabela → dodanie brakujących kolumn + `ALTER COLUMN` (typ/null/default)
  * DROP brakujących kolumn jest wykonywany **po procedurach**, tylko gdy `FB_DESTRUCTIVE=1`, z kontrolą zależności; blokady trafiają do raportu/dry-run.

* **Procedures**

  * dwie fazy: stub (nagłówek + puste body) → właściwe body (`CREATE OR ALTER PROCEDURE ...`)
  * DROP z kontrolą zależności; w razie błędu body popraw skrypt i uruchom `update-db` ponownie (stub zostaje).

* **DROP obiektów**

  * brak pliku = kandydat do DROP (domeny/tabele/procedury) - realny DROP tylko gdy `FB_DESTRUCTIVE=1`.
  * obsługiwane są też pliki `DROP ...` w tych samych folderach (hybryda: stan docelowy + migracje).
  * DROP pomijany przy zależnościach (raport/dry-run); heurystyka rename jest informacyjna - RENAME wykonujesz ręcznie. Rename kolumn/tabel nie migruje danych automatycznie (traktowane jako add+drop w trybie destrukcyjnym).

## Scenariusze i oczekiwane zachowanie (skrót)

* Zmiana typu/default/null/validation domeny → ALTER DOMAIN (raport "Zmodyfikowane domeny").
* Dodanie kolumny → ALTER TABLE ADD.
* Zmiana typu/default/null kolumny → ALTER COLUMN.
* Usunięcie kolumny → kandydat do DROP; realny DROP tylko z `FB_DESTRUCTIVE=1` i brakiem zależności.
* Zmiana ciała procedury → CREATE OR ALTER (stuby + pełne ciała).
* Brak zmian → pliki pominięte, plan pusty w dry-run.
* Dry-run → tylko plan SQL + blokady zależności + kandydaci do DROP (bez modyfikacji bazy).

### Raporty i logi

* `build-db` drukuje: ścieżkę do pliku bazy, liczbę wykonanych plików, liczbę błędów oraz szczegóły błędnych plików; w razie błędów kończy się wyjątkiem.
* `update-db` drukuje: liczbę wykonanych plików, liczbę akcji SQL, pominięte pliki, błędy oraz ostrzeżenia parsowania. W zależności od trybu pojawiają się też:
  * sekcje dodanych/zmodyfikowanych/usuniętych obiektów (domen, tabel, procedur, kolumn),
  * kandydaci do DROP przy wyłączonej destrukcji,
  * sugestie rename przy braku destrukcji,
  * sekcja `DEBUG COMPARE` przy `FB_DEBUG_COMPARE=1`,
  * ostrzeżenia po weryfikacji, jeśli wykryto niespójności po `FB_RECHECK=1`,
  * w trybie dry-run – plan instrukcji oraz blokady zależności zamiast wykonania,
  * w razie błędów lista plików wraz z komunikatami i zakończenie wyjątkiem.

## Przykładowy scenariusz użycia

1. Utwórz w bazie przykładowe tabele i procedury (np. w IBExpert).
2. Wygeneruj z niej skrypty:

   ```bash
   dotnet run export-scripts --connection-string "<...>" --output-dir ".\scripts"
   ```
3. Dodaj wygenerowane pliki do repozytorium git.
4. Zmodyfikuj pliki `.sql` (np. dodaj domenę, dodaj kolumnę do tabeli, zmień treść procedury).
5. Zaktualizuj bazę:

   ```bash
   dotnet run update-db --connection-string "<...>" --scripts-dir ".\scripts"
   ```

## Known limitations (MVP + tryby)

* Operacje DROP wymagają `FB_DESTRUCTIVE=1` i braku zależności; inaczej trafiają do raportu/dry-run.
* Heurystyka rename jest wyłącznie podpowiedzią; ewentualne zmiany nazw trzeba wykonać ręcznie.
* Tryb docelowego stanu: brak pliku = kandydat do DROP (tylko z FB_DESTRUCTIVE=1). Dodatkowo obsługiwane są jawne pliki DROP.
* Export/Update skupia się na domenach, tabelach (kolumnach) i procedurach.
* Skrypty powinny być "1 plik = 1 obiekt".
* Constraints/triggers/indexes są poza zakresem (nie są eksportowane ani synchronizowane) i mogą blokować część operacji ALTER/DROP — narzędzie raportuje błąd Firebirda.
* Przed użyciem `FB_DESTRUCTIVE=1` zrób kopię pliku `.fdb`.
* Uruchamiaj update, gdy baza jest w spoczynku (procedury/tabele nie są używane), bo DDL może się wywalić na „object is in use”.


## Examples

Repozytorium zawiera gotowy zestaw przykładowych skryptów w `src/Examples/Library` oraz `src/Examples/Library_Update` (domains/tables/procedures),
które można wykorzystać do szybkiego testu:

```bash
dotnet run build-db --db-dir ".\data" --scripts-dir ".\src\Examples\Library"
dotnet run update-db --connection-string "database=localhost/3050:C:\path\to\database.fdb;user=SYSDBA;password=<YOUR_PASSWORD>" --scripts-dir ".\src\Examples\Library_Update"
