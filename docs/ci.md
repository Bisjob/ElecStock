# CI

Two workflows, one per forge. The repository lives on a self-hosted Forgejo, with a push mirror to GitHub:

- **Forgejo Actions**, `.forgejo/workflows/ci.yml` (§1–6): the development pipeline. Build and tests of every push and
  pull request, the server image in the Forgejo registry (`edge`, releases), the Android app on a self-hosted F-Droid
  repository (optional).
- **GitHub Actions**, `.github/workflows/release.yml` (§7): the public releases, on a version tag pushed by the mirror.
  Tests, the multi-arch image on ghcr.io, the signed APK, the GitHub release.

Each forge only reads its own folder: GitHub ignores `.forgejo/`, and Forgejo ignores `.github/workflows` as long as
`.forgejo/workflows` exists. In both, the image is built by the .NET SDK, not by a Dockerfile, and nothing specific to
an installation is in the files: addresses, paths and keys are Actions variables and secrets.

## 1. What runs

`.forgejo/workflows/ci.yml`, for a push on `main`, a `v*` tag, a pull request, or by hand (*Run workflow*, on the
branch of your choice). A push on another branch starts nothing (`on: push` lists `main` and the tags): its pull
request builds it, or run the workflow on it.

| Job | What it does |
|---|---|
| `build-test` | Builds `src/ElecStock.Server` and runs every `*.Tests` project (`dotnet test --project`, JUnit reports in the `test-results` artifact). The service tests run on a PostgreSQL 17 installed and started in the job (apt.postgresql.org), on `localhost:5432` (`ELECSTOCK_TEST_DB`), not on a service container (see below). |
| `android` | Only for a tag `vX.Y.Z` (not a pre-release) and a manual run. Installs the JDK 17 (Temurin), the `maui-android` workload and the Android packages the app needs (`InstallAndroidDependencies`), then publishes `src/ElecStock.Appli` for `net10.0-android`: one APK (arm64 and x64), signed with the release key, in the `android-apk` artifact. A tag also adds it to the F-Droid repository when `FDROID_DIR` is set (§4). Nothing is cached: about 1 GB is downloaded per run. |
| `docker image` | Builds the server image with the .NET SDK (`dotnet publish -t:PublishContainer`, the container settings of `ElecStock.Server.csproj`), for linux-x64. It pushes it for a tag `vX.Y.Z` (tags `X.Y.Z`, `X.Y`, `latest`; a pre-release such as `v1.1.0-rc.1` gets its own tag only) and for a push on `main` (tag `edge`). Pull requests and manual runs write the image to an archive in the job and push nothing. |

- **Version**: a tag `v1.1.0` builds 1.1.0; anything else builds `<Version of Directory.Build.props>-ci.<run number>`.
  After a release, raise that `Version` (1.0.0 → 1.0.1): otherwise `main` (`edge`) is built as `1.0.0-ci.<run>`,
  which reads as older than the 1.0.0 already released. The commit is added by the SDK (Source Link): `+<sha>` in the informational version, and on the image the labels
  `org.opencontainers.image.revision` and `.source` (`PublishRepositoryUrl=true`).
- **Android version**: `versionName` is the version; `versionCode` is `X*10000 + Y*100 + Z` (1.1.0 → 10100), so it goes
  up with every release (minor and patch below 100). A manual run builds `1.1.0-ci.<run>` with the code of 1.1.0. The
  csproj's `ApplicationDisplayVersion` / `ApplicationVersion` only apply to local builds. `AndroidManifest.xml` must not
  set `android:versionCode`: it would win over `ApplicationVersion` and every release would be `1`. The application id
  is the manifest's `package`, `com.bisjob.elecstock` (lowercase), which also names the APK.
- **Image**: `<registry>/<owner, lowercase>/elecstock-server`, in the container registry of the Forgejo that runs the
  workflow, shown under the repository's *Packages*. `<registry>` is the variable `REGISTRY` when set, else the host of
  `github.server_url` (§3).
- **No Docker in the jobs**: the SDK pulls the base image (`mcr.microsoft.com/dotnet/aspnet:10.0`), adds the published
  server and pushes the result to the registry itself, over HTTP.
- **Service tests**: with `ELECSTOCK_TEST_DB` set, `StockServiceTests` fails when the database does not answer instead
  of skipping itself as it does on a PC without the dev database, so a broken database shows as a red build.
- **Why no PostgreSQL service container**: the runner puts the jobs on a shared Docker network (`forgejo`), where the
  service alias `postgres` can also name another job's service (a concurrent or cancelled run) or any other container
  called `postgres`. The tests then reached several servers in turn and failed at random with
  `database "elecstock_test" does not exist`. A service alias of the run is no way out: the runner refuses
  `--network-alias` in the service options. The step prints what `postgres` names on that network.
- **Actions**: `actions/checkout` comes from Forgejo's mirror (`data.forgejo.org`); `actions/setup-dotnet` and
  `actions/setup-java` are taken from GitHub; the test results and the APK use `forgejo/upload-artifact@v4`, since
  `actions/upload-artifact@v4` does not work with Forgejo. An action that calls api.github.com sends it the job's
  `GITHUB_TOKEN`, a Forgejo token that GitHub refuses (401): that is why `setup-java` installs Temurin, whose versions
  come from api.adoptium.net, and not Microsoft's OpenJDK ("Could not load manifest for Microsoft Build of OpenJDK").

## 2. Runner

The workflow asks for the label `ubuntu-latest` and runs its jobs in containers. A `forgejo-runner` with the label `ubuntu-latest` on `catthehacker/ubuntu:act-22.04` does it, registered
on this repository, or on your user (`/user/settings/actions/runners`) to serve all your repositories.

Only the F-Droid step (optional, §4) uses the host's Docker in the job (`docker create` of the `fdroidserver` image,
with a host folder): the runner's configuration must then hand the socket to the jobs (`container.docker_host: automount` and
`/var/run/docker.sock` in `valid_volumes`); mounting it in the runner's own
container is not enough. The test database is started in the job, and the server image is built by the SDK.
No extra runner is needed for Android: the job installs its tools itself. A runner whose image already holds the JDK,
the workload and the Android SDK would only save the downloads.

## 3. Publishing the image

1. An access token that can write packages: *Settings › Applications › Generate new token*, scope **`write:package`**.
   The automatic token of a workflow is limited to its repository and does not reach the registry.
2. In the repository, *Settings › Actions › Secrets*: add **`REGISTRY_TOKEN`** with that token (or once on your user,
   for every repository). The workflow logs in as the repository's owner.
3. Without the secret, a build that should publish fails at *Check the registry token*; pull requests and manual runs
   do not need it.
4. **The registry's name**: the image is named after the registry, and pulled under that name. When the runner reaches
   Forgejo by a name the other machines do not know (a runner on Forgejo's Docker network uses its container name,
   e.g. `forgejo:3000`), set *Settings › Actions › Variables* › **`REGISTRY`** to the address the machines pulling the
   image use, `host:port` (e.g. `forgejo.lan:3000`), on the repository or your user. The SDK pushes from the job
   container: that address must be reachable from the jobs too (a LAN address is).

### Registry over plain HTTP

The workflow lists the registry in `DOTNET_CONTAINER_INSECURE_REGISTRIES`, so the SDK pushes to it over HTTP. The
machines pulling the image (the server running `docker-compose.yml`) still need it in Docker's insecure registries, in
`/etc/docker/daemon.json` (Docker Desktop: *Settings › Docker Engine*), then a Docker restart:

```json
{ "insecure-registries": ["<forgejo host>:<port>"] }
```

## 4. Publishing the Android app on F-Droid

A tag `vX.Y.Z` builds the APK, then adds it to a self-hosted F-Droid repository (a folder served over HTTP, updated
by an `fdroidserver` image on the runner's host). Optional: without the variable `FDROID_DIR`, the step is skipped and
the APK stays a run artifact (the public one is on the GitHub release, §7). Once:

1. **The signing key.** Every release must be signed with the same key: the F-Droid client does not offer an update
   signed by another one. Keep the keystore and its password outside the repository, with a backup: without them, the
   next release can only be installed after uninstalling the app (and losing its settings).
   ```bash
   keytool -genkeypair -keystore elecstock.keystore -alias elecstock -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=ElecStock"
   ```
   `keytool` comes with any JDK; without one:
   `docker run --rm -it -v "$PWD:/out" -w /out eclipse-temurin:17-jre-alpine keytool …` (same arguments). It asks
   for the password (PKCS12 keystore: the same one for the store and the key).
2. In the repository (or your user), *Settings › Actions › Secrets*:
   - **`ANDROID_KEYSTORE`**: the keystore in base64, on one line: `base64 -w0 elecstock.keystore`
     (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("$PWD\elecstock.keystore"))`);
   - **`ANDROID_KEYSTORE_PASSWORD`**: its password.

   Another alias than `elecstock`: variable `ANDROID_KEY_ALIAS`.
3. *Settings › Actions › Variables* › **`FDROID_DIR`**: the path **on the host** of the F-Droid folder, the one the
   F-Droid server mounts on `/repo` (`<compose data path>/fdroid/data`, written out in full).
4. An image with `fdroidserver` installed must exist on the runner's host: `fdroidserver-local` by default, another
   name in the variable `FDROID_IMAGE`.

What the *Publish to the F-Droid repository* step does: it copies the APK into `repo/` as
`com.bisjob.elecstock_<versionCode>.apk` (one file per version, owned by `1000:100` like the rest of the folder),
then runs `fdroid update --create-metadata` in a container of `fdroidserver-local` (user `1000:100`, through the
host's Docker), which adds the APK and rebuilds and signs the index. The first time, it also creates
`metadata/com.bisjob.elecstock.yml`: fill in its summary and description there if you want them in the client.

- **Without the secrets**, a release fails at *Signing key*; a manual run still builds, signed with a throwaway debug
  key (warning), to check the build.
- **Pre-release tags** (`v1.1.0-rc.1`) are not published: the client would offer them as an update.
- **An ElecStock already on the phone** from a local build (Visual Studio's debug key) is not updated by F-Droid:
  uninstall it once, then install it from F-Droid.
- **Release build**: trimming and AOT apply, unlike a debug build. Install the APK of a manual run (*Actions › CI ›
  Run workflow*, artifact `android-apk`) before the first release.

## 5. Deploying the image

`docker-compose.yml` pulls `${REPO_URL}/elecstock-server:${ELECSTOCK_TAG}`, by default the public image
`ghcr.io/bisjob/elecstock-server:latest` (§7). To use the Forgejo one instead, set in the server's `.env` `REPO_URL` to
`<registry>/<owner, lowercase>` (e.g. `forgejo.lan:3000/owner`), log in once (`docker login <registry>`, with a token
that can read packages), then `docker compose pull && docker compose up -d`. `latest` is the last release (`vX.Y.Z`
tag); `X.Y.Z` and `X.Y` pin one (`ELECSTOCK_TAG`); `edge` is the last commit of `main`, not released (Forgejo only).

Without the CI, the README's *Quick start* section builds the same image by hand.

## 6. Running the workflow locally

`forgejo-runner exec` runs the jobs on the local Docker without a server, copying the working tree instead of checking
out. From the repository root:

```bash
docker run --rm -u 0:0 -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:/work" -w /work \
  data.forgejo.org/forgejo/runner:13.2.1 \
  forgejo-runner exec -W .forgejo/workflows/ci.yml -j build-test -E push -i docker.io/catthehacker/ubuntu:act-22.04 \
  --forgejo-instance http://localhost:3000
```

- Git Bash on Windows: prefix with `MSYS_NO_PATHCONV=1`.
- `--forgejo-instance` gives the actions a server address (nothing is sent there): without it `setup-dotnet` stops at
  `TypeError: Invalid URL` (`/api/v1`).
- Add `-s GITHUB_TOKEN=not-a-github-token`: on the server the jobs always have a token, and an action that sends it to
  github.com fails there while passing locally without it.
- There is no artifact server in this mode: *Upload test results* fails, so the job ends red after the tests, and the
  `docker image` job is skipped. To run both, use a copy of the workflow without that step
  (`-v <folder of the copy>:/wf`, `-W /wf/ci.yml`).
- `exec` ignores the `branches` / `tags` filters: `-E push` runs the jobs on the checked-out branch, and on the
  default branch the `docker image` job pushes it as `edge`: point it at a throwaway registry,
  `docker run -d --rm --name ci-registry -p 5099:5000 registry:2`, and add
  `--var REGISTRY=host.docker.internal:5099 -s REGISTRY_TOKEN=dummy`. `exec` takes the owner from the `origin` remote
  and gets it wrong: `http:` or `https:` for an `http(s)://` address, and the push fails on the name
  `http-/elecstock-server`. Run it on a clone whose remote is written `git@<host>:<owner>/ElecStock-App.git` (it is not
  contacted): the owner is then `git@<host>:<owner>`, which the SDK turns into a valid name, and the push goes through.
- The `android` job runs with `-j android -E workflow_dispatch` on a copy of the workflow without `needs:`/`if:` (and
  without *Upload the APK*), the version written in place of `needs.build-test.outputs.version`. To test the signing,
  pass a throwaway keystore: `-s ANDROID_KEYSTORE="$(base64 -w0 test.keystore)" -s ANDROID_KEYSTORE_PASSWORD=…`.
  The F-Droid step only runs on a push.

## 7. GitHub releases

`.github/workflows/release.yml`, for a tag `v*` (pushed to GitHub by the Forgejo mirror) or by hand (*Run workflow*,
builds and tests everything, publishes nothing, the APK as a run artifact).

| Job | What it does |
|---|---|
| `build-test` | Builds the server and runs the tests, the service tests on a `postgres:17-alpine` service (`localhost:5432`). |
| `docker image` | Builds the server image for `linux-x64` and `linux-arm64` (one image index, `ContainerRuntimeIdentifiers`) and pushes it to `ghcr.io/<owner>/elecstock-server`: tags `X.Y.Z`, `X.Y`, `latest` (a pre-release, its own tag only). Logs in with the job's `GITHUB_TOKEN` (`packages: write`). |
| `android` | Same build as on Forgejo (same `versionCode`), signed with the same key: an APK from GitHub and one from F-Droid update each other. |
| `release` | Creates the GitHub release of the tag (`ElecStock X.Y.Z`, a pre-release for `vX.Y.Z-…`), with the APK `ElecStock-X.Y.Z.apk`, the `docker pull` line and the notes generated from the commits. |

Once:

1. **The mirror**: on Forgejo, *Settings › Repository › Mirror settings* › push mirror to
   `https://github.com/<owner>/<repo>.git`, with a GitHub token (fine-grained: *Contents* and *Workflows* read and
   write on that repository; without *Workflows*, GitHub refuses any push that touches `.github/workflows`). Tick
   **Sync when commits are pushed**, otherwise tags only reach GitHub at the next periodic sync.
2. **The secrets**, in the GitHub repository, *Settings › Secrets and variables › Actions*: `ANDROID_KEYSTORE` and
   `ANDROID_KEYSTORE_PASSWORD`, the same as on Forgejo (§4). Another alias: variable `ANDROID_KEY_ALIAS`. The image
   needs nothing: `GITHUB_TOKEN` is enough.
3. **The package's visibility**: after the first release, check that the package `elecstock-server` (the owner's
   *Packages* tab) is **public**, so that `docker pull` works without logging in (*Package settings › Change
   visibility*). The image's `org.opencontainers.image.source` label links it to the repository.

A release is then: tag `vX.Y.Z` on Forgejo and push it. Forgejo builds its own image and the F-Droid APK, the mirror
pushes the tag to GitHub, and GitHub builds the public image and the release. Raise `Version` in
`Directory.Build.props` afterwards (§1).
