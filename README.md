
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

#### Konfiguracja logowania dla build-db

Build używa zmiennych środowiskowych:

PowerShell:

```powershell
$env:FB_USER="SYSDBA"
$env:FB_PASSWORD="<YOUR_PASSWORD>"
$env:FB_HOST="localhost"
```

Opcjonalnie nazwa pliku bazy:

```powershell
$env:DB_FILE_NAME="database.fdb"
```

### 3) Update - aktualizacja istniejącej bazy na podstawie skryptów

Wykonuje skrypty w kolejności: `domains → tables → procedures`.

```bash
dotnet run update-db \
  --connection-string "database=localhost/3050:C:\path\to\DATABASE.FDB;user=SYSDBA;password=<YOUR_PASSWORD>" \
  --scripts-dir ".\Examples\Library"
```

## Jak działa update (zasady)

* **Domains**

  * jeśli domena istnieje i skrypt jest `CREATE DOMAIN`, to jest pomijany (MVP/bezpiecznie).

* **Tables**

  * jeśli tabela nie istnieje: wykonuje `CREATE TABLE ...`
  * jeśli tabela istnieje: dodaje brakujące kolumny (`ALTER TABLE ... ADD ...`)
  * nie usuwa kolumn i nie zmienia typów istniejących kolumn (non-destructive update).

* **Procedures**

  * wykonywane idempotentnie: `CREATE OR ALTER PROCEDURE ...`

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

* Update nie usuwa obiektów/kolumn (brak operacji destrukcyjnych).
* Export/Update skupia się na domenach, tabelach (kolumnach) i procedurach.
* Skrypty powinny być “1 plik = 1 obiekt”.


## Examples

Repozytorium zawiera gotowy zestaw przykładowych skryptów w `Examples/Library` (domains/tables/procedures),
który można wykorzystać do szybkiego testu:

```bash
dotnet run build-db --db-dir ".\data" --scripts-dir ".\Examples\Library"
dotnet run update-db --connection-string "database=localhost/3050:C:\path\to\database.fdb;user=SYSDBA;password=<YOUR_PASSWORD>" --scripts-dir ".\Examples\Library"
