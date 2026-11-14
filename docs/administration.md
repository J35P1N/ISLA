# Administration guide

Deployment, account recovery, storage changes, and complete backups for self-hosted ISLA installations. For initial setup, see the [README](../README.md).

## Accounts, backup and recovery

The monogram labelled **Account** in the bottom navigation opens your signed-in profile and **Sign out**. Administrators see **Settings** for household administration; members see **Account settings** for their own password. Settings lets every user change their password. Administrators can create accounts, change Administrator/Member roles, disable accounts, reset passwords and revoke all of a user's sessions. The last active administrator cannot be disabled or demoted. Password changes, resets, role changes and disabling an account revoke its existing sessions. Open interactive pages revalidate each minute, and every household operation checks its session immediately. ASP.NET Core Identity handles password hashing, validation and lockout through a transaction-scoped storage adapter shared by JSON and SQLite. Existing password hashes and the 12–256 character password policy remain compatible. Five failed password attempts lock the account for 15 minutes.

**Remember this browser** uses a fixed 90-day expiry from sign-in, with no sliding extension. Unchecked logins use a session cookie and a maximum server lifetime of 24 hours. Logout revokes the current session. HTTPS is required for deployed use; terminate TLS at your trusted reverse proxy or configure HTTPS directly.

In JSON mode, the private data directory contains `recipes.json`, `identity.json` (password hashes and revocable sessions), `installation.json`, and `keys/` (Data Protection keys). Unix files are restricted to their owner. Keep the entire directory private, writable by the application user, and backed up together while ISLA is stopped. Existing household files are preserved during setup, with a `recipes.json.pre-setup.bak` backup. Invalid or missing configured files show an HTTP 503 recovery page rather than reopening setup or replacing data. Do not edit these files while ISLA is running.

Admin **Settings → Export household** downloads a versioned JSON export of recipes, goods, plans, extras, overrides, pantry, purchases and prices. Accounts, hashes, browser sessions, secrets and keys are excluded. **Import** validates the export and accepts only an empty household, with a 10 MB limit. Failed validation leaves the destination unchanged. To move between JSON and SQLite: export, stop/reconfigure/restart into an empty destination, initialise its administrator, then import. Keep the old volume until the import is verified; recreate member accounts at the destination.

For a forgotten administrator password, stop ISLA first and create a restricted file containing the new password (12–256 characters). Run the operator command against the same data directory:

```sh
ISLA_RESET_USERNAME=admin ISLA_RESET_PASSWORD_FILE=/private/new-password \
dotnet run --project src/ISLA --no-launch-profile -- --reset-admin
```

For a container, with the normal service stopped:

```sh
docker compose run --rm --no-deps -e ISLA_RESET_USERNAME=admin \
  -e ISLA_RESET_PASSWORD_FILE=/run/secrets/reset-password \
  -v /private/new-password:/run/secrets/reset-password:ro isla --reset-admin
```

The command resets and re-enables the existing administrator and revokes all of their sessions. Supply `DataDirectory` when using a custom location, ensure the process user can read the password file, and remove that file afterwards. Members ask an administrator to reset their passwords. No email service is required.

For operator-managed mounted configuration, set `ISLA_CONFIG_FILE` to a read-only, restricted JSON file outside the web root, for example `{"DataDirectory":"/app/data","Setup":{"TokenFile":"/run/secrets/isla-setup-token"}}`. Environment variables and command-line arguments override that file. These storage settings are read-only in the UI and changes require a restart. Credentials must stay in restricted files or injected environment secrets.


## SQLite storage

Choose **Medium — SQLite** during first-run setup, or set `Storage__Provider=Sqlite` in the environment (`Storage:Provider` in JSON configuration). With no deployment override, the wizard saves the choice in the private `storage.json` file. The environment, command line, and operator-mounted configuration take precedence and make the provider field read-only in setup and Settings. JSON remains the default for existing installations.

When manually testing separate installations on the same browser address, close old ISLA tabs and load `/login` afresh after switching. Submitting a form rendered by the previous installation can fail antiforgery validation because the stores have different keys or signed-in identities.

The database lives at `DataDirectory/isla.db`; `sqlite-installation.json` records that the database has been created so a missing database triggers recovery. Accounts and sessions are in SQLite, separate from household export data. Keep the complete directory, including `storage.json`, `sqlite-installation.json`, `keys/`, and any SQLite journal/WAL files. **Stop ISLA before copying or restoring a backup.** Back up before upgrades. SQLite must use a **local persistent Docker volume**, not NFS, SMB, a shared network filesystem, or multiple application instances. The exclusive directory lock also applies to setup, upgrade, and recovery commands.

For a new Compose deployment with the provider fixed by the operator, install Docker Engine/Desktop with the Compose plugin, clone this repository, and run these commands from its root:

```sh
docker compose -f compose.yaml -f compose.sqlite.yaml up -d --build
docker compose -f compose.yaml -f compose.sqlite.yaml logs isla
```

Open http://localhost:8080 and enter the latest generated setup token from the logs. Create an administrator with a password of 12–256 characters, confirm the fixed SQLite provider, validate, review, and initialise. Sign in and check **Settings → Household storage → Active provider: Sqlite**. No SQLite installation on the host or database connection string is needed.

Use the same Compose files for subsequent commands:

```sh
# Stop and remove the container, retaining its data volume:
docker compose -f compose.yaml -f compose.sqlite.yaml down

# Recreate the container using the existing store:
docker compose -f compose.yaml -f compose.sqlite.yaml up -d

# Inspect startup or recovery messages:
docker compose -f compose.yaml -f compose.sqlite.yaml logs isla
```

Keep the same Compose project name and volume when restarting or upgrading. A different checkout directory can change the default project name and create a separate empty volume; use `--project-name` consistently if you set it explicitly. After setup, container replacement should retain accounts, data, and remembered sign-ins without reopening setup.

Both Compose modes mount `recipe-data` at `/app/data`, which survives container recreation. Do not use `down -v` when retaining data. The SQLite override selects the provider; it does not migrate or delete JSON files. For mounted configuration, include `"Storage":{"Provider":"Sqlite"}` in the restricted configuration file. The wizard verifies writable access but never provisions volumes.

To transfer an existing JSON household:

1. Sign in as an administrator and export from **Settings → Export household**. Keep a stopped backup of the old volume.
2. In **Settings → Household storage**, select SQLite and save. This schedules a change for the next restart; the running process continues using JSON. If deployment configuration controls the provider, change that configuration instead.
3. Restart ISLA. The SQLite destination must be empty. Complete setup with its administrator and import the saved household export. Accounts and sessions are deliberately not transferred; recreate other users.
4. Verify recipes, plans, shopping, stock and prices, and keep the original store until satisfied. ISLA never deletes the previous JSON files.

For a new destination directory or volume, configure `DataDirectory` and `Storage__Provider` before startup, initialise, then import. Moving back to JSON requires a new empty directory if the original JSON files are still present. Settings refuses to overwrite existing destination files. To roll back using the retained original store, stop ISLA, restore its complete backup or explicitly select its provider in deployment configuration, and restart. Changes made in SQLite are not automatically copied back.

New SQLite schemas are created in a staging file during wizard validation and published only after migrations and the installation row are complete. Interrupted staging files are discarded on retry; published schemas can resume administrator setup without creating duplicate accounts. Existing schemas are validated at startup and **never upgraded automatically**. Unknown, missing or damaged configured storage returns HTTP 503 recovery without falling back to JSON or reopening setup. After backing up and stopping the service, apply supported schema migrations explicitly:

```sh
Storage__Provider=Sqlite DataDirectory=/private/isla-data \
dotnet run --project src/ISLA --no-launch-profile -- --upgrade-storage

# With the normal Compose service stopped:
docker compose -f compose.yaml -f compose.sqlite.yaml run --rm --no-deps isla --upgrade-storage
```

Restart the service after a successful upgrade. The upgrade command also checks an already current database. A future schema requires a compatible ISLA release; never edit migration history or reset an installation to work around recovery. Administrator password recovery uses the same `--reset-admin` command described above. For operator-controlled SQLite, stop the service and include its override in the recovery command:

```sh
docker compose -f compose.yaml -f compose.sqlite.yaml down
docker compose -f compose.yaml -f compose.sqlite.yaml run --rm --no-deps \
  -e ISLA_RESET_USERNAME=admin \
  -e ISLA_RESET_PASSWORD_FILE=/run/secrets/reset-password \
  -v /private/new-password:/run/secrets/reset-password:ro isla --reset-admin
docker compose -f compose.yaml -f compose.sqlite.yaml up -d
```

Replace `admin` with your existing administrator username. The password file must be readable by the container's normal application user; run recovery as that user, rather than overriding it with root. Remove the password file after successful recovery.



## Complete Docker backup and restore

Household export is useful for transferring between providers, but it does not back up accounts, sessions, or encryption keys. For disaster recovery, stop ISLA and archive its **entire data volume**. These steps work for both JSON and SQLite.

The example below assumes a source Compose project named `isla`, whose volume is `isla_recipe-data`. Replace those two names with your actual project and volume names. Use `docker volume ls` and inspect your application container’s mounts to identify its data volume. Do not guess the source volume name: Docker can create an empty volume if it does not exist.

From the repository root, build a utility image, create a private backup directory, stop the source, and archive it:

```sh
docker build -t isla:backup-tools .
mkdir -p "$HOME/isla-backups"
chmod 700 "$HOME/isla-backups"
docker compose --project-name isla -f compose.yaml down
docker volume inspect isla_recipe-data
docker run --rm --user 0:0 --entrypoint tar \
  --mount type=volume,source=isla_recipe-data,target=/source,readonly \
  --mount type=bind,source="$HOME/isla-backups",target=/backup \
  isla:backup-tools -czf /backup/isla-backup.tar.gz -C /source .
tar -tzf "$HOME/isla-backups/isla-backup.tar.gz"
docker compose --project-name isla -f compose.yaml up -d
```

For operator-controlled SQLite, include `-f compose.sqlite.yaml` after `-f compose.yaml` in each Compose command above. Keep any other deployment overrides as well. The archive name is reused by these commands; choose a new filename for each backup you intend to retain. Store a copy away from the Docker host. The archive contains sensitive account data and keys.

To exercise recovery, stop the source again to free port 8080. Restore into a **new, empty** project named `isla-restore`; the following example uses operator-controlled SQLite:

```sh
docker compose --project-name isla -f compose.yaml -f compose.sqlite.yaml down
docker compose --project-name isla-restore -f compose.yaml -f compose.sqlite.yaml create --build
docker run --rm --user 0:0 --entrypoint tar \
  --mount type=volume,source=isla-restore_recipe-data,target=/destination \
  --mount type=bind,source="$HOME/isla-backups",target=/backup,readonly \
  isla:backup-tools -xzpf /backup/isla-backup.tar.gz -C /destination
docker compose --project-name isla-restore -f compose.yaml -f compose.sqlite.yaml up -d
```

For a JSON backup, omit `-f compose.sqlite.yaml` from those commands. Use an application image compatible with the backed-up schema. Root is used only by the archive helper to preserve file ownership; the application continues to run as its normal non-root user. Never extract a backup over a running or populated installation.

Open http://localhost:8080, sign in with the backed-up administrator credentials, and verify the active provider, recipes, plans, and shopping data. Setup should remain closed. After checking the restore, stop it and restart the source using its original Compose files. Keep the backup until recovery has been verified.

`docker compose down` retains data. **`docker compose down --volumes` permanently deletes that project's data volumes**; use it only when intentionally removing an installation. Removing a container or image alone does not remove its named data volume.

