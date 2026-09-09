# Validation status / État de validation

## English

Evidence from local development on Windows x64:

| Check | Result |
| --- | --- |
| Release build | Passed, zero warnings and errors |
| Standalone portable executable self-tests | Passed |
| Parser and input guards | Supported stereo structure, truncation, duplicate fields and wrong routing tested |
| DSP tests | FFT/convolution, WAV output, silence/bypass, cross-routing, invalid samples, tail, validation mismatch and cancellation tested |
| Recovery tests | 12 isolated scratch-profile scenarios passed, plus a throwing progress callback |
| Worker startup without parent permission | Refused after the timeout; no plugin loaded |
| Real native VST2 rendering | Dirac Live Processor 1.8.2, stereo, 48 kHz; 32,768-frame export |
| Independent rendering comparison | Worst relative RMS error approximately −97.54 dB; proposed preamp −9 dB |
| Checked existing Dirac settings and preset | File hashes unchanged after native rendering test |
| Interface | Rendered and visually inspected |

**Not yet verified:** the full conversion service with temporary-slot staging on a live user profile; other plugin versions, sample rates and PCs; actual playback through the user's APO route; acoustic benefit.

Recovery tests use temporary directories, not live audio settings. The native rendering test reused an existing preset without staging another one. These are different tests and must not be treated as full end-to-end profile validation.

The numerical result applies to the tested filter and test signal. It is not a universal fidelity guarantee. Personal filters, settings, captures and diagnostic reports are intentionally not committed.

## Français

Preuves de développement local sous Windows x64 :

| Vérification | Résultat |
| --- | --- |
| Compilation Release | Réussie, sans erreur ni avertissement |
| Tests de l’exécutable portable autonome | Réussis |
| Analyse du fichier et gardes d’entrée | Structure stéréo, troncature, champs dupliqués et routage incorrect testés |
| Tests DSP | FFT/convolution, WAV, silence/bypass, chemins croisés, valeurs invalides, queue, écart de validation et annulation testés |
| Tests de récupération | 12 scénarios sur profils temporaires réussis, plus un callback de progression défaillant |
| Worker sans autorisation du parent | Refus après le délai prévu ; aucun plugin chargé |
| Rendu VST2 natif réel | Dirac Live Processor 1.8.2, stéréo, 48 kHz ; export de 32 768 trames |
| Comparaison avec un rendu indépendant | Pire erreur RMS relative d’environ −97,54 dB ; préampli proposé −9 dB |
| Réglages Dirac et preset existant contrôlés | Empreintes identiques après le test de rendu natif |
| Interface | Rendue et examinée visuellement |

**Pas encore vérifiés :** le service complet avec emplacement temporaire sur un profil utilisateur réel ; les autres versions, fréquences et PC ; la lecture réelle via le trajet APO de l’utilisateur ; le bénéfice acoustique.

Les tests de récupération utilisent des dossiers temporaires, pas les réglages audio actifs. Le test de rendu natif a réutilisé un preset existant sans créer d’autre emplacement. Ces tests sont distincts et ne constituent pas une validation complète du parcours sur profil réel.

Le résultat numérique concerne le filtre et le signal testés. Ce n’est pas une garantie universelle de fidélité. Les filtres, réglages, captures et rapports de diagnostic personnels ne sont volontairement pas versionnés.
