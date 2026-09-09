# Dirac → APO

**A small Windows app that turns a Dirac filter into a standalone Equalizer APO convolution filter.**  
**Une petite application Windows qui transforme un filtre Dirac en convolution autonome pour Equalizer APO.**

[English](#english) · [Français](#francais)

> Experimental Windows x64 tool. An installed, activated **Dirac Live Processor VST2** is required **only for conversion**. No proprietary plugin, license, or personal filter is included in this repository.
>
> Outil expérimental Windows x64. Le **VST2 Dirac Live Processor**, installé et activé, est nécessaire **uniquement pendant la conversion**. Ce dépôt ne contient aucun plugin propriétaire, aucune licence ni aucun filtre personnel.

<a id="english"></a>
## English

### What does it do?

The app sends digital impulses through the installed Dirac plugin and captures its complete left and right responses. It then checks the resulting filter against a separate plugin rendering of a test signal.

On success, it creates:

- A **stereo float32 WAV** containing the impulse responses, including phase correction and relative channel delays/gains.
- An **Equalizer APO `.txt` configuration** with a sample-rate guard and calculated preamp headroom.
- A **JSON validation report** with the input checksum, capture settings and numerical error.

The WAV works in Equalizer APO **without a VST and without Dirac Live Processor running**. The app does not open an audio device or send sound to your speakers. It uses the authorized processor rather than reconstructing Dirac's proprietary processing algorithm from the `.bin` coefficients.

### Requirements

**To use a portable build:**

- Windows x64.
- **Dirac Live Processor VST2 x64**, installed and activated for your Windows account. The usual plugin path is:
  ```text
  C:\Program Files\Common Files\VST2\DiracLiveProcessor.dll
  ```
- A supported Dirac `.bin` export: **`CARDRTRP` version 2**, with independent left/right stereo correction.
- At least one unused Dirac filter slot. The tool only claims a slot whose directory does not exist; it never overwrites an existing preset.
- Equalizer APO installed separately to use the exported filter.

A self-contained portable build does **not** require Python, REAPER or a separate .NET installation. Building from source requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### Convert a filter

1. Close Dirac Live, Dirac Live Processor, Equalizer APO Configuration Editor, and all other applications hosting the Dirac plugin. Keep them closed until conversion finishes.
2. Run `DiracToApo.exe`. The current interface is in French.
3. Select the `.bin`, the VST2 `.dll`, the output folder and the sample rate. Supported choices are **32,000 / 44,100 / 48,000 Hz**, provided that rate is present in the source filter.
4. Tick the confirmation box and click **Convertir**. Plugin initialization takes several seconds.
5. Wait for a validated result. **Annuler** cancels the job and waits for cleanup. A failed numerical check does not produce a successful export.

The application does not edit Equalizer APO's active configuration for you.

### Use the result in Equalizer APO

1. Disable the Dirac VST block to avoid applying the correction twice.
2. Set the Windows playback device to the **same sample rate as the WAV**.
3. Add the generated `.txt` file through an `Include` entry. Use **Copier la ligne Include** to copy its full path. Example:
   ```text
   Include: C:\Filters\Dirac\generated_filter.txt
   ```
   For Equalizer APO 1.2.1, do **not** add quotation marks around this path; spaces are part of the parameter.
4. Start at a low listening volume and verify that the correction is active.

Keep the generated `.txt` and `.wav` together. The configuration already includes preamp attenuation. Other filters in your APO chain can require more headroom. If the sample rate does not match, the generated configuration deliberately skips the correction.

**The WAV is a convolution filter, not an audio track to play.**

### How your settings are protected

The converter temporarily stages the selected filter in an unused slot. It backs up two Dirac settings files, records a recovery journal, and restores those settings after the worker stops. It does not copy authentication tokens, change Windows volume, edit APO's `config.txt`, change permissions or reconfigure audio devices.

The native plugin runs in a separate process. It cannot load until the parent records its process identity and grants permission. Cleanup refuses to run while that worker is still active. Slot ownership markers and backup checksums protect against overwriting unrelated files. Conflicts are retained for recovery rather than silently deleted.

Working files, original/changed settings copies and the journal remain in:

```text
%LOCALAPPDATA%\DiracToApo
```

If a crash leaves `pending.json`, close all plugin hosts and try another conversion. Recovery runs before the new input is validated. If recovery fails, **keep the journal and backups**. Unknown hosts cannot all be detected automatically: the confirmation that you closed them matters. Cross-volume profile staging is refused rather than replaced with an unsafe copy.

These folders can contain your filter and settings. **Do not upload them to Git.**

### Validation and limitations

- Tested locally with **Dirac Live Processor 1.8.2 VST2 x64**, a stereo preset and **48 kHz**. Other versions, machines and sample rates are not yet confirmed by live plugin tests.
- The C# renderer produced a 32,768-frame filter. Its worst relative RMS error against an independent plugin render was approximately **−97.5 dB** on the test signal. Checked Dirac settings files and the existing preset were unchanged after that rendering test.
- The exporter requires an error of **−70 dB or lower on each channel**. It also checks for non-finite values, silence, plausible bypass, channel mixing and an unfinished impulse tail.
- Recovery, ownership conflicts and backup protections have isolated synthetic tests. The full temporary-slot workflow has **not yet been validated on a live user profile**. See [VALIDATION.md](VALIDATION.md).
- No VST3, raw miniDSP `.bin`, multichannel routing or Bass Control matrix export.
- A fixed FIR cannot fully reproduce nonlinear, dynamic or time-varying processing. Such processing may fail validation.
- Delays are preserved, not removed by independently aligning channel peaks. The capture's temporary global gain is removed from the exported response.
- This is a **numerical conversion check**, not proof of acoustic improvement or proof that APO is active on your playback path.
- Portable builds are unsigned. Do not disable Windows security protections to run them.

### Build and test

From the folder containing `DiracToApo.csproj`, on Windows with the .NET 10 SDK:

```powershell
dotnet restore DiracToApo.csproj
dotnet build DiracToApo.csproj -c Release --no-restore
& .\bin\Release\net10.0-windows\win-x64\DiracToApo.exe --self-test
dotnet publish DiracToApo.csproj -c Release --no-restore -o dist
```

The portable executable is `dist\DiracToApo.exe`. No third-party NuGet packages are used. Build/publish may download Microsoft's SDK/runtime packs if they are not cached.

Self-tests use temporary synthetic data and profiles, without loading the plugin or changing your audio configuration. They can additionally verify existing local capture fixtures, if available; no personal fixtures are distributed. CI builds, runs the self-tests and produces an unsigned Windows artifact. Executables belong in build artifacts or releases, not in the source history.

---

<a id="francais"></a>
## Français

### Quel est le principe ?

L’application fait traiter des impulsions numériques par le plugin Dirac installé et capture ses réponses gauche et droite complètes. Elle vérifie ensuite le filtre obtenu contre un rendu indépendant du plugin sur un signal de test.

En cas de succès, elle crée :

- Un **WAV stéréo float32** contenant les réponses impulsionnelles, avec la correction de phase et les différences de gain/délai entre canaux.
- Une **configuration Equalizer APO `.txt`** avec une garde de fréquence et une marge de préamplification calculée.
- Un **rapport JSON** avec l’empreinte du fichier source, les paramètres de capture et l’erreur numérique.

Le WAV fonctionne dans Equalizer APO **sans VST et sans Dirac Live Processor actif**. Aucun périphérique audio n’est ouvert et aucun son n’est envoyé aux enceintes. Le programme utilise le processeur autorisé au lieu de tenter de reconstituer l’algorithme propriétaire à partir des coefficients du `.bin`.

### Prérequis

**Pour utiliser une version portable :**

- Windows x64.
- Le **VST2 Dirac Live Processor x64**, installé et activé pour votre compte Windows. Chemin habituel :
  ```text
  C:\Program Files\Common Files\VST2\DiracLiveProcessor.dll
  ```
- Un export Dirac `.bin` compatible : **`CARDRTRP` version 2**, avec correction stéréo gauche/droite indépendante.
- Au moins un emplacement Dirac inutilisé. Le programme utilise uniquement un emplacement dont le dossier n’existe pas ; il n’écrase jamais un preset existant.
- Equalizer APO installé séparément pour utiliser le résultat.

L’exécutable portable autonome ne demande **ni Python, ni REAPER, ni installation séparée de .NET**. La compilation des sources nécessite le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

### Convertir un filtre

1. Fermez Dirac Live, Dirac Live Processor, l’éditeur de configuration Equalizer APO et tous les autres logiciels qui utilisent le plugin Dirac. Ne les rouvrez pas avant la fin.
2. Lancez `DiracToApo.exe`. L’interface actuelle est en français.
3. Choisissez le `.bin`, le plugin VST2 `.dll`, le dossier de sortie et la fréquence. Choix proposés : **32 000 / 44 100 / 48 000 Hz**, si cette fréquence est présente dans le filtre source.
4. Cochez la confirmation et cliquez sur **Convertir**. L’initialisation du plugin prend plusieurs secondes.
5. Attendez le résultat validé. **Annuler** interrompt le travail et attend le nettoyage. Une validation numérique échouée ne produit pas d’export présenté comme réussi.

L’application ne modifie pas automatiquement votre configuration APO active.

### Utiliser le résultat dans Equalizer APO

1. Désactivez le bloc VST Dirac pour éviter une double correction.
2. Réglez la sortie audio Windows sur **la même fréquence que le WAV**.
3. Ajoutez le fichier `.txt` généré avec une instruction `Include`. Le bouton **Copier la ligne Include** copie son chemin complet. Exemple :
   ```text
   Include: C:\Filters\Dirac\generated_filter.txt
   ```
   Avec Equalizer APO 1.2.1, **n’ajoutez pas de guillemets** autour du chemin ; les espaces font partie du paramètre.
4. Commencez à faible volume et vérifiez que la correction agit réellement.

Gardez le `.txt` et le `.wav` dans le même dossier. La configuration inclut déjà une atténuation de préamplification. Les autres filtres APO peuvent nécessiter davantage de marge. Si la fréquence ne correspond pas, la configuration générée ignore volontairement la correction.

**Le WAV est un filtre de convolution, pas une piste audio à écouter.**

### Protection des réglages

Le convertisseur place temporairement le filtre choisi dans un emplacement inutilisé. Il sauvegarde deux fichiers de réglages Dirac, écrit un journal de récupération, puis restaure les réglages après l’arrêt du processus de conversion. Il ne copie pas les jetons d’authentification, ne change pas le volume Windows, ne modifie pas `config.txt`, les permissions ou les périphériques audio.

Le plugin fonctionne dans un processus séparé. Il ne peut se charger qu’après l’enregistrement de son identité par le programme principal et son autorisation explicite. La restauration est refusée tant que ce processus est actif. Des marqueurs de propriété et des empreintes de sauvegarde protègent les fichiers tiers. Les conflits sont conservés pour récupération, pas supprimés silencieusement.

Les fichiers de travail, les copies de réglages avant/après et le journal restent dans :

```text
%LOCALAPPDATA%\DiracToApo
```

Si un arrêt brutal laisse `pending.json`, fermez tous les logiciels utilisant le plugin et relancez une conversion. La récupération précède la validation du nouveau fichier. Si elle échoue, **conservez le journal et les sauvegardes**. Tous les hôtes VST ne peuvent pas être détectés automatiquement : votre confirmation de fermeture est importante. Un déplacement temporaire entre volumes est refusé plutôt que remplacé par une copie risquée.

Ces dossiers peuvent contenir votre filtre et vos réglages. **Ne les publiez pas sur Git.**

### Validation et limites

- Test local avec **Dirac Live Processor 1.8.2 VST2 x64**, un preset stéréo et **48 kHz**. Les autres versions, PC et fréquences ne sont pas encore confirmés par des tests réels du plugin.
- Le moteur C# a produit un filtre de 32 768 trames. Sa pire erreur RMS relative par rapport au rendu indépendant du plugin était d’environ **−97,5 dB** sur le signal de test. Les fichiers de réglages contrôlés et le preset existant étaient inchangés après ce test de rendu.
- L’export exige une erreur **inférieure ou égale à −70 dB sur chaque canal**. Il vérifie aussi les valeurs non finies, le silence, un bypass plausible, le mélange de canaux et une réponse non terminée.
- La récupération, les conflits de propriété et la protection des sauvegardes ont des tests synthétiques isolés. Le parcours complet avec emplacement temporaire **n’a pas encore été validé sur un profil utilisateur réel**. Voir [VALIDATION.md](VALIDATION.md).
- Pas de VST3, de `.bin` miniDSP brut, de routage multicanal ou de matrice Bass Control.
- Un FIR fixe ne reproduit pas complètement un traitement non linéaire, dynamique ou variable dans le temps. Ces traitements peuvent échouer à la validation.
- Les délais sont conservés, sans recaler séparément les pics gauche/droite. Le gain global temporaire de capture est retiré du résultat.
- Il s’agit d’une **vérification numérique de conversion**, pas d’une preuve d’amélioration acoustique ni d’activation d’APO sur votre trajet audio.
- Les exécutables portables ne sont pas signés. Ne désactivez pas les protections Windows pour les lancer.

### Compiler et tester

Depuis le dossier contenant `DiracToApo.csproj`, sous Windows avec le SDK .NET 10 :

```powershell
dotnet restore DiracToApo.csproj
dotnet build DiracToApo.csproj -c Release --no-restore
& .\bin\Release\net10.0-windows\win-x64\DiracToApo.exe --self-test
dotnet publish DiracToApo.csproj -c Release --no-restore -o dist
```

L’exécutable portable se trouve dans `dist\DiracToApo.exe`. Aucun package NuGet tiers n’est utilisé. La compilation/publication peut télécharger les packs SDK/runtime Microsoft s’ils ne sont pas déjà en cache.

Les tests utilisent des données et profils synthétiques temporaires, sans charger le plugin ni changer votre configuration audio. Ils peuvent aussi vérifier des captures locales existantes ; aucune capture personnelle n’est distribuée. Le workflow CI compile, lance les tests et produit un artefact Windows non signé. Les exécutables doivent rester dans les artefacts ou releases, pas dans l’historique des sources.

---

### Project status / Statut du projet

Independent experimental tool. Not affiliated with or endorsed by Dirac or Equalizer APO. Dirac software and licenses are not redistributed.  
Outil expérimental indépendant. Non affilié à Dirac ou Equalizer APO, et non approuvé par ces projets. Aucun logiciel ni aucune licence Dirac n’est redistribué.
