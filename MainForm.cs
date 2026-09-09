using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiracToApo;

public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(27, 39, 56);
    private static readonly Color Muted = Color.FromArgb(83, 98, 118);
    private static readonly Color Blue = Color.FromArgb(28, 91, 184);
    private readonly IConversionService _service;
    private readonly TableLayoutPanel _inputs;
    private readonly TextBox _filter = PathBox("Fichier de filtre Dirac");
    private readonly TextBox _plugin = PathBox("Plugin Dirac Live Processor");
    private readonly TextBox _output = PathBox("Dossier de sortie");
    private readonly ComboBox _sampleRate = new ComboBox();
    private readonly CheckBox _closedApps = new CheckBox();
    private readonly Button _convert = ActionButton("&Convertir", true);
    private readonly Button _cancel = ActionButton("&Annuler");
    private readonly Label _status = BodyLabel("Choisissez votre filtre, puis confirmez la fermeture des logiciels.");
    private readonly ProgressBar _progress = new ProgressBar();
    private readonly TableLayoutPanel _results;
    private readonly Label _resultSummary = BodyLabel("");
    private readonly TextBox _resultPaths = new TextBox();
    private readonly Button _openFolder = ActionButton("&Ouvrir le dossier");
    private readonly Button _copyInclude = ActionButton("Copier la ligne &Include");
    private CancellationTokenSource? _cancellation;
    private ConversionResult? _result;
    private bool _busy;
    private bool _closeAfterJob;
    private int _activeRun;

    public MainForm(IConversionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Text = "Dirac → APO";
        Font = new Font("Segoe UI", 10F);
        ForeColor = Ink;
        BackColor = Color.FromArgb(246, 248, 251);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(850, 750);
        MinimumSize = new Size(800, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) };
        var root = Stack();
        root.Dock = DockStyle.Top;
        root.TabIndex = 0;
        scroll.Controls.Add(root);
        Controls.Add(scroll);

        var title = BodyLabel("Dirac → APO");
        title.Font = new Font("Segoe UI", 23F, FontStyle.Bold);
        title.ForeColor = Ink;
        title.Margin = new Padding(0, 0, 0, 5);
        AddRow(root, title);
        var intro = BodyLabel("Convertissez un filtre Dirac en convolution pour Equalizer APO.\nPlugin nécessaire seulement à la conversion. Aucun son n’est diffusé.");
        intro.Margin = new Padding(0, 0, 0, 18);
        AddRow(root, intro);

        _inputs = Stack();
        _inputs.BackColor = Color.White;
        _inputs.Padding = new Padding(16, 12, 16, 12);
        _inputs.Margin = new Padding(0, 0, 0, 12);
        _inputs.TabIndex = 0;
        var files = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 0,
            Margin = Padding.Empty, TabIndex = 0
        };
        files.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        files.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(files, "&Filtre .bin", _filter, BrowseFilter, 0);
        AddPathRow(files, "&Plugin .dll", _plugin, BrowsePlugin, 1);
        AddPathRow(files, "&Sortie", _output, BrowseOutput, 2);
        AddRow(_inputs, files);

        var rateRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 5, 0, 4), TabIndex = 1
        };
        var rateLabel = BodyLabel("&Fréquence");
        rateLabel.Margin = new Padding(0, 5, 12, 0);
        rateLabel.TabIndex = 0;
        _sampleRate.DropDownStyle = ComboBoxStyle.DropDownList;
        _sampleRate.Items.AddRange(new object[] { "32 000 Hz", "44 100 Hz", "48 000 Hz" });
        _sampleRate.SelectedIndex = 2;
        _sampleRate.Width = 140;
        _sampleRate.AccessibleName = "Fréquence d’échantillonnage";
        _sampleRate.TabIndex = 1;
        rateRow.Controls.Add(rateLabel);
        rateRow.Controls.Add(_sampleRate);
        AddRow(_inputs, rateRow);

        _closedApps.Text = "J’ai fermé Dirac, l’éditeur APO et les logiciels qui utilisent le plugin";
        _closedApps.AccessibleName = _closedApps.Text;
        _closedApps.AutoSize = true;
        _closedApps.Dock = DockStyle.Fill;
        _closedApps.Margin = new Padding(0, 10, 0, 4);
        _closedApps.TabIndex = 2;
        _closedApps.CheckedChanged += (_, _) => _convert.Enabled = !_busy && _closedApps.Checked;
        AddRow(_inputs, _closedApps);
        var required = BodyLabel("Confirmation obligatoire avant la conversion.");
        required.Font = new Font("Segoe UI", 9F);
        required.Margin = new Padding(23, 0, 0, 0);
        AddRow(_inputs, required);
        AddRow(root, _inputs);

        var safety = BodyLabel("Un filtre temporaire utilise un slot vide. Les réglages du plugin sont sauvegardés puis restaurés. La configuration APO n’est jamais modifiée automatiquement.");
        safety.Font = new Font("Segoe UI", 9F);
        safety.Margin = new Padding(0, 0, 0, 12);
        AddRow(root, safety);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, WrapContents = true,
            Margin = new Padding(0, 0, 0, 8), TabIndex = 1
        };
        _convert.Enabled = false;
        _convert.TabIndex = 0;
        _cancel.Enabled = false;
        _cancel.TabIndex = 1;
        _convert.Click += async (_, _) => await ConvertAsync();
        _cancel.Click += (_, _) => CancelConversion();
        actions.Controls.Add(_convert);
        actions.Controls.Add(_cancel);
        AddRow(root, actions);
        AcceptButton = _convert;

        _status.Margin = new Padding(0, 0, 0, 6);
        _status.AccessibleName = "État de la conversion";
        _status.AccessibleRole = AccessibleRole.StaticText;
        AddRow(root, _status);
        _progress.Dock = DockStyle.Top;
        _progress.Height = 7;
        _progress.Margin = new Padding(0, 0, 0, 12);
        _progress.AccessibleName = "Progression de la conversion";
        AddRow(root, _progress);

        _results = Stack();
        _results.Visible = false;
        _results.TabIndex = 2;
        _results.Padding = new Padding(12);
        _results.BackColor = Color.White;
        _results.Margin = new Padding(0, 0, 0, 12);
        _resultSummary.ForeColor = Ink;
        AddRow(_results, _resultSummary);
        _resultPaths.Multiline = true;
        _resultPaths.ReadOnly = true;
        _resultPaths.WordWrap = false;
        _resultPaths.ScrollBars = ScrollBars.Both;
        _resultPaths.Dock = DockStyle.Top;
        _resultPaths.Height = 82;
        _resultPaths.BackColor = Color.White;
        _resultPaths.BorderStyle = BorderStyle.FixedSingle;
        _resultPaths.AccessibleName = "Fichiers créés : WAV, configuration APO et rapport de validation";
        _resultPaths.TabIndex = 0;
        _resultPaths.Margin = new Padding(0, 8, 0, 8);
        AddRow(_results, _resultPaths);
        var resultActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Margin = Padding.Empty, TabIndex = 1
        };
        _openFolder.TabIndex = 0;
        _copyInclude.TabIndex = 1;
        _openFolder.Click += (_, _) => OpenOutputFolder();
        _copyInclude.Click += (_, _) => CopyInclude();
        resultActions.Controls.Add(_openFolder);
        resultActions.Controls.Add(_copyInclude);
        AddRow(_results, resultActions);
        AddRow(root, _results);

        var reminder = BodyLabel("Dans Windows, utilisez la même fréquence que le WAV. Désactivez le VST Dirac avant d’ajouter la ligne Include dans APO, pour éviter une double correction. Le préampli est déjà inclus dans le fichier de configuration.");
        reminder.Font = new Font("Segoe UI", 9F);
        AddRow(root, reminder);

        var knownFilter = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dirac", "Dirac_Live_Processor", "filters", "0", "filter.bin");
        if (File.Exists(knownFilter)) _filter.Text = knownFilter;
        _plugin.Text = @"C:\Program Files\Common Files\VST2\DiracLiveProcessor.dll";
        _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiracToAPO");
        FormClosing += OnFormClosing;
        Shown += (_, _) => _filter.Focus();
    }

    private static TableLayoutPanel Stack() => new TableLayoutPanel
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, RowCount = 0, Dock = DockStyle.Top, Margin = Padding.Empty,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) }
    };

    private static void AddRow(TableLayoutPanel parent, Control control)
    {
        var row = parent.RowCount++;
        parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parent.Controls.Add(control, 0, row);
    }

    private static Label BodyLabel(string text) => new Label
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Muted,
        Margin = Padding.Empty, UseMnemonic = true
    };

    private static TextBox PathBox(string accessibleName) => new TextBox
    {
        Dock = DockStyle.Fill, AccessibleName = accessibleName,
        Margin = new Padding(0, 5, 8, 5)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(102, 34), Padding = new Padding(10, 3, 10, 3),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Blue : Color.White,
            ForeColor = primary ? Color.White : Ink,
            UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 8, 0)
        };
        button.FlatAppearance.BorderColor = primary ? Blue : Color.FromArgb(206, 215, 228);
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static void AddPathRow(TableLayoutPanel table, string labelText, TextBox path, EventHandler browse, int index)
    {
        var label = BodyLabel(labelText);
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.TabIndex = index * 3;
        path.TabIndex = index * 3 + 1;
        var button = ActionButton("Parcourir…");
        button.AccessibleName = "Parcourir : " + path.AccessibleName;
        button.TabIndex = index * 3 + 2;
        button.Margin = new Padding(0, 2, 0, 4);
        button.Click += browse;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, index);
        table.Controls.Add(path, 1, index);
        table.Controls.Add(button, 2, index);
    }

    private void BrowseFilter(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choisir le filtre Dirac", Filter = "Filtre Dirac (*.bin)|*.bin",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        if (File.Exists(_filter.Text)) dialog.FileName = _filter.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _filter.Text = dialog.FileName;
    }

    private void BrowsePlugin(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choisir DiracLiveProcessor.dll", Filter = "Plugin VST2 (*.dll)|*.dll",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        if (File.Exists(_plugin.Text)) dialog.FileName = _plugin.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _plugin.Text = dialog.FileName;
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisir le dossier de sortie", UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_output.Text)) dialog.SelectedPath = _output.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
    }

    private async Task ConvertAsync()
    {
        if (_busy || !_closedApps.Checked) return;
        int[] rates = { 32000, 44100, 48000 };
        var request = new ConversionRequest(_filter.Text.Trim(), _plugin.Text.Trim(),
            _output.Text.Trim(), rates[_sampleRate.SelectedIndex], _closedApps.Checked);
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var run = ++_activeRun;
        _busy = true;
        _inputs.Enabled = false;
        _convert.Enabled = false;
        _cancel.Enabled = true;
        _result = null;
        _results.Visible = false;
        _progress.Value = 0;
        _status.ForeColor = Muted;
        _status.Text = "Préparation de la conversion…";
        var progress = new Progress<ConversionProgress>(update =>
        {
            if (!_busy || run != _activeRun || IsDisposed || token.IsCancellationRequested) return;
            _progress.Value = Math.Clamp(update.Percent, 0, 100);
            _status.Text = update.Message;
        });
        try
        {
            _result = await _service.ConvertAsync(request, progress, token);
            _progress.Value = 100;
            _status.ForeColor = Blue;
            _status.Text = "Conversion terminée. Aucun changement n’a été appliqué à APO.";
            var culture = CultureInfo.GetCultureInfo("fr-FR");
            _resultSummary.Text = $"WAV et configuration prêts · {request.SampleRate.ToString("N0", culture)} Hz\n" +
                $"Préampli inclus : {_result.PreampDb.ToString("0.00", culture)} dB. " +
                $"Validation : écart maximal {_result.WorstRelativeErrorDb.ToString("0.000", culture)} dB (détails dans le rapport).";
            _resultPaths.Text = $"WAV : {_result.WavPath}\r\nConfiguration : {_result.ConfigPath}\r\nValidation : {_result.ReportPath}";
            _resultPaths.SelectionStart = 0;
            _resultPaths.SelectionLength = 0;
            _results.Visible = true;
        }
        catch (OperationCanceledException)
        {
            _status.ForeColor = Muted;
            _status.Text = "Conversion annulée. Le nettoyage est terminé.";
            _progress.Value = 0;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.FromArgb(159, 48, 48);
            _status.Text = "La conversion n’a pas abouti. Vérifiez le message ci-dessous avant de réessayer.";
            _progress.Value = 0;
            if (!_closeAfterJob)
                MessageBox.Show(this, ex.Message, "Conversion impossible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            ++_activeRun;
            _busy = false;
            _cancellation.Dispose();
            _cancellation = null;
            _inputs.Enabled = true;
            _cancel.Enabled = false;
            // A fresh confirmation is required for each conversion.
            _closedApps.Checked = false;
            _convert.Enabled = false;
            if (_closeAfterJob) BeginInvoke(new Action(Close));
        }
    }

    private void CancelConversion()
    {
        if (!_busy || _cancellation == null || _cancellation.IsCancellationRequested) return;
        _cancel.Enabled = false;
        _status.ForeColor = Muted;
        _status.Text = "Annulation demandée… Restauration des réglages et nettoyage en cours.";
        _cancellation.Cancel();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        if (_closeAfterJob) return;
        if (MessageBox.Show(this,
            "Une conversion est en cours. Voulez-vous l’annuler et quitter ?\n\nLa fenêtre restera ouverte jusqu’à la fin du nettoyage et de la restauration des réglages.",
            "Annuler et quitter", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _closeAfterJob = true;
        // Completion can occur while the confirmation dialog pumps UI messages.
        if (!_busy) BeginInvoke(new Action(Close));
        else CancelConversion();
    }

    private void OpenOutputFolder()
    {
        if (_result == null) return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_result.ConfigPath));
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true, Verb = "open" });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible d’ouvrir le dossier.\n\n" + ex.Message,
                "Dossier de sortie", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyInclude()
    {
        if (_result == null) return;
        try
        {
            var configPath = Path.GetFullPath(_result.ConfigPath);
            if (configPath.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidOperationException("Le chemin de configuration contient un retour à la ligne.");
            // APO treats the entire parameter as a path, including any quotation marks.
            Clipboard.SetText("Include: " + configPath);
            _status.Text = "Ligne Include copiée. Désactivez d’abord le VST Dirac dans APO.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible de copier la ligne Include.\n\n" + ex.Message,
                "Presse-papiers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
