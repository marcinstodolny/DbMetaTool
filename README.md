
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

````

Każdy plik powinien zawierać **jedno polecenie SQL** (np. `CREATE DOMAIN ...`, `CREATE TABLE ...`, `CREATE OR ALTER PROCEDURE ...`).
To upraszcza wykonanie skryptów bez parsowania wielu statementów w jednym pliku.

## Komendy

### 1) Export skryptów z istniejącej bazy

Generuje pliki `.sql` w strukturze `domains/tables/procedures`.

```bash
dotnet run export-scripts \
  --connection-string "database=localhost/3050:C:\path\to\TEST.FDB;user=SYSDBA;password=<YOUR_PASSWORD>" \
  --output-dir ".\out"
````

Efekt:

```
out/
  domains/
  tables/
  procedures/
```

### 2) Build - utworzenie nowej bazy na podstawie skryptów

Tworzy pustą bazę w `--db-dir` i wykonuje skrypty z `--scripts-dir`.

```bash
dotnet run build-db \
  --db-dir ".\data" \
  --scripts-dir ".\Examples\Library"
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
  --scripts-dir ".\Examples\Library"
```

Procedury są wykonywane w **dwóch fazach**:
1. Stub nagłówka (`CREATE OR ALTER PROCEDURE ... AS BEGIN END`) - aby umożliwić kompilację zależnych obiektów.
2. Docelowe body - właściwa treść procedury.

Jeśli w drugiej fazie wystąpi błąd body, w bazie pozostanie stub (puste body). Popraw skrypt procedury i uruchom `update-db` ponownie; w razie potrzeby przywróć poprzednią wersję procedury ręcznie przed ponowną próbą.

### Zmienne środowiskowe Update

* `FB_DRY_RUN` - ustaw `1`, aby wykonać tylko plan i raport (bez SQL, z listą blokad zależności / kandydatów do DROP).
* `FB_DESTRUCTIVE` - ustaw `1`, aby zezwolić na operacje DROP (obiekty i brakujące kolumny) tam, gdzie nie ma zależności.

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
```

## Jak działa update (zasady)

* **Domains**

  * jeśli domena istnieje i skrypt jest `CREATE DOMAIN`, to jest pomijany (MVP/bezpiecznie).

* **Tables**

  * jeśli tabela nie istnieje: wykonuje `CREATE TABLE ...`
  * jeśli tabela istnieje: dodaje brakujące kolumny (`ALTER TABLE ... ADD ...`) i zmienia typ/null/default istniejących kolumn (ALTER COLUMN)
  * DROP brakujących kolumn jest wykonywany **po procedurach**, tylko gdy `FB_DESTRUCTIVE=1`, z kontrolą zależności; blokady zależności trafiają do raportu.

* **Procedures**

  * wykonywane w dwóch fazach: najpierw stub (nagłówek + puste body), potem właściwe body (`CREATE OR ALTER PROCEDURE ...`)
  * w przypadku błędu body - popraw skrypt i uruchom `update-db` ponownie (stub pozostaje, aby zależności mogły się kompilować)

* **DROP obiektów**

  * usuwanie brakujących domen/tabel/procedur jest wykonywane tylko gdy `FB_DESTRUCTIVE=1`
  * DROP jest pomijany, gdy istnieją zależne obiekty; informacja trafia do raportu
  * heurystyka rename (tabele/procedury) jest tylko informacyjna - narzędzie nie wykonuje RENAME

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

## Known limitations (MVP)

* Operacje DROP (kolumny/obiekty) wymagają `FB_DESTRUCTIVE=1` i mogą być blokowane przez zależności - wtedy pojawią się tylko w raporcie.
* Heurystyka rename jest wyłącznie podpowiedzią; ewentualne zmiany nazw trzeba wykonać ręcznie.
* Export/Update skupia się na domenach, tabelach (kolumnach) i procedurach.
* Skrypty powinny być "1 plik = 1 obiekt".


## Examples

Repozytorium zawiera gotowy zestaw przykładowych skryptów w `Examples/Library` oraz `Examples/Library_Update` (domains/tables/procedures),
które można wykorzystać do szybkiego testu:

```bash
dotnet run build-db --db-dir ".\data" --scripts-dir ".\Examples\Library"
dotnet run update-db --connection-string "database=localhost/3050:C:\path\to\database.fdb;user=SYSDBA;password=<YOUR_PASSWORD>" --scripts-dir ".\Examples\Library_Update"
