using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MitViewer;

/// <summary>768x512 の仮想画面上に画像を配置し、ドラッグで表示位置を変更できるビュー。</summary>
internal sealed class ScreenView : Control
{
    private Bitmap? _image;
    private Point _origin;
    private float _scale = 1f;
    private bool _dragging;
    private Point _grabOffset;

    public event EventHandler? OriginChanged;

    public ScreenView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(48, 48, 48);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Bitmap? Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Point Origin
    {
        get => _origin;
        set { _origin = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float ViewScale
    {
        get => _scale;
        set { _scale = Math.Max(0.1f, value); Invalidate(); }
    }

    /// <summary>画面全体がウィンドウに収まる倍率。</summary>
    public float FitScale => Math.Max(0.1f, Math.Min(
        (float)(ClientSize.Width - 8) / MitImage.ScreenWidth,
        (float)(ClientSize.Height - 8) / MitImage.ScreenHeight));

    private Rectangle ScreenRect
    {
        get
        {
            int w = (int)Math.Round(MitImage.ScreenWidth * _scale);
            int h = (int)Math.Round(MitImage.ScreenHeight * _scale);
            return new Rectangle(Math.Max(0, (ClientSize.Width - w) / 2), Math.Max(0, (ClientSize.Height - h) / 2), w, h);
        }
    }

    private Rectangle ImageRect
    {
        get
        {
            if (_image is null) return Rectangle.Empty;
            Rectangle s = ScreenRect;
            return new Rectangle(
                s.X + (int)Math.Round(_origin.X * _scale),
                s.Y + (int)Math.Round(_origin.Y * _scale),
                (int)Math.Round(_image.Width * _scale),
                (int)Math.Round(_image.Height * _scale));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle screen = ScreenRect;
        e.Graphics.FillRectangle(Brushes.Black, screen);

        if (_image is not null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_image, ImageRect);
        }

        using var pen = new Pen(Color.DimGray);
        e.Graphics.DrawRectangle(pen, screen.X, screen.Y, screen.Width - 1, screen.Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _image is not null && ImageRect.Contains(e.Location))
        {
            Rectangle r = ImageRect;
            _grabOffset = new Point(e.X - r.X, e.Y - r.Y);
            _dragging = true;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && _image is not null)
        {
            Rectangle s = ScreenRect;
            int x = (int)Math.Round((e.X - _grabOffset.X - s.X) / _scale);
            int y = (int)Math.Round((e.Y - _grabOffset.Y - s.Y) / _scale);
            SetOriginClamped(x - Mod(x, MitImage.OriginXAlignment), y);
        }
        else
        {
            Cursor = _image is not null && ImageRect.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
        }
        base.OnMouseUp(e);
    }

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    private void SetOriginClamped(int x, int y)
    {
        if (_image is null) return;
        var next = new Point(
            Math.Clamp(x, 0, Math.Max(0, MitImage.ScreenWidth - _image.Width)),
            Math.Clamp(y, 0, Math.Max(0, MitImage.ScreenHeight - _image.Height)));
        if (next == _origin) return;
        _origin = next;
        Invalidate();
        OriginChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class MainForm : Form
{
    private readonly ScreenView _view = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _originLabel = new();
    private readonly ToolStripMenuItem _fitMenu = new("ウィンドウに合わせる(&F)");
    private readonly List<ToolStripMenuItem> _zoomItems = new();

    private MitImage? _image;
    private Bitmap? _bitmap;
    private string? _path;
    private int _zoom = 1;
    private bool _fit;
    private bool _modified;

    public MainForm(string? initialFile)
    {
        Text = "MIT Viewer";
        ClientSize = new Size(820, 640);
        AllowDrop = true;

        _view.Dock = DockStyle.Fill;
        _view.OriginChanged += (_, _) =>
        {
            if (_image is null) return;
            _image.SetOrigin(_view.Origin.X, _view.Origin.Y);
            _modified = true;
            UpdateStatus();
        };
        _view.SizeChanged += (_, _) => ApplyScale();

        _status.Items.Add(_statusLabel);
        _status.Items.Add(_originLabel);

        Controls.Add(_view);
        Controls.Add(BuildToolStrip());
        Controls.Add(_status);
        MainMenuStrip = BuildMenu();
        Controls.Add(MainMenuStrip);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        if (!string.IsNullOrEmpty(initialFile)) OpenFile(initialFile);
        else _statusLabel.Text = "ファイルを開くか、.mit ファイルをドロップしてください。";
    }

    private ToolStrip BuildToolStrip()
    {
        var strip = new ToolStrip();
        strip.Items.Add(new ToolStripButton("中央に配置(&C)", null, (_, _) => CenterImage())
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "画像を 768x512 画面の中央へ移動します"
        });
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(new ToolStripLabel("画像をドラッグして表示位置を変更できます（X は 8 ドット単位）"));
        return strip;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("開く(&O)...", null, (_, _) => ShowOpenDialog())
        {
            ShortcutKeys = Keys.Control | Keys.O
        });
        file.DropDownItems.Add(new ToolStripMenuItem("画像から変換して開く(&I)...", null, (_, _) => ImportBmp()));
        file.DropDownItems.Add(new ToolStripMenuItem("上書き保存(&S)", null, (_, _) => Save(_path))
        {
            ShortcutKeys = Keys.Control | Keys.S
        });
        file.DropDownItems.Add(new ToolStripMenuItem("名前を付けて保存(&A)...", null, (_, _) => SaveAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("PNG として保存(&P)...", null, (_, _) => SaveAsPng()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("終了(&X)", null, (_, _) => Close()));

        var view = new ToolStripMenuItem("表示(&V)");
        foreach (int z in new[] { 1, 2, 3, 4 })
        {
            int zoom = z;
            var item = new ToolStripMenuItem($"{zoom}00%", null, (_, _) => SetZoom(zoom));
            _zoomItems.Add(item);
            view.DropDownItems.Add(item);
        }
        _fitMenu.Click += (_, _) => SetFit(!_fit);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_fitMenu);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("背景色を変更(&B)...", null, (_, _) => ChangeBackColor()));

        var edit = new ToolStripMenuItem("編集(&E)");
        edit.DropDownItems.Add(new ToolStripMenuItem("パレットを変更(&P)...", null, (_, _) => ChangePalette()));

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        UpdateViewChecks();
        return menu;
    }

    private void ShowOpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "mint タイトル画像 (*.mit)|*.mit|すべてのファイル (*.*)|*.*",
            Title = "MIT 画像を開く"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenFile(dialog.FileName);
    }

    private void OpenFile(string path)
    {
        if (!ConfirmDiscardChanges()) return;
        try
        {
            MitImage image = MitImage.Load(path);
            ShowImage(image, path, modified: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ImportBmp()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "画像ファイル (*.bmp;*.png;*.mag)|*.bmp;*.png;*.mag|ビットマップ画像 (*.bmp)|*.bmp|PNG 画像 (*.png)|*.png|MAG 画像 (*.mag)|*.mag|すべてのファイル (*.*)|*.*",
            Title = "変換する画像を開く"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportImageFile(dialog.FileName);
    }

    private void ImportImageFile(string path)
    {
        if (!ConfirmDiscardChanges()) return;
        try
        {
            using Bitmap source = Path.GetExtension(path).Equals(".mag", StringComparison.OrdinalIgnoreCase)
                ? MagImage.Load(path)
                : new Bitmap(path);
            MitImage image = MitImage.FromBitmap(source, Path.GetFileNameWithoutExtension(path));
            ShowImage(image, path: null, modified: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "変換エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowImage(MitImage image, string? path, bool modified)
    {
        Bitmap bitmap = image.ToBitmap();

        _bitmap?.Dispose();
        _image = image;
        _bitmap = bitmap;
        _path = path;
        _modified = modified;
        _view.Image = bitmap;
        _view.Origin = new Point(image.OriginX, image.OriginY);
        Text = $"MIT Viewer - {(path is null ? "無題" : Path.GetFileName(path))}";

        if (image.Width > MitImage.ScreenWidth || image.Height > MitImage.ScreenHeight)
            MessageBox.Show(this,
                $"画像サイズ {image.Width}x{image.Height} が画面 {MitImage.ScreenWidth}x{MitImage.ScreenHeight} を超えています。",
                "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        ApplyScale();
        UpdateStatus();
    }

    private void CenterImage()
    {
        if (_image is null) return;
        _image.CenterOrigin();
        _view.Origin = new Point(_image.OriginX, _image.OriginY);
        _modified = true;
        UpdateStatus();
    }

    private void SaveAs()
    {
        if (_image is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "mint タイトル画像 (*.mit)|*.mit",
            FileName = _path is null ? "image.mit" : Path.GetFileName(_path)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) Save(dialog.FileName);
    }

    private void Save(string? path)
    {
        if (_image is null) return;
        if (string.IsNullOrEmpty(path)) { SaveAs(); return; }
        try
        {
            _image.Save(path);
            _path = path;
            _modified = false;
            Text = $"MIT Viewer - {Path.GetFileName(path)}";
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveAsPng()
    {
        if (_bitmap is null) return;
        using var dialog = new SaveFileDialog { Filter = "PNG 画像 (*.png)|*.png", FileName = "image.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _bitmap.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetZoom(int zoom)
    {
        _zoom = zoom;
        _fit = false;
        UpdateViewChecks();
        ApplyScale();
    }

    private void SetFit(bool fit)
    {
        _fit = fit;
        UpdateViewChecks();
        ApplyScale();
    }

    private void UpdateViewChecks()
    {
        for (int i = 0; i < _zoomItems.Count; i++) _zoomItems[i].Checked = !_fit && _zoom == i + 1;
        _fitMenu.Checked = _fit;
    }

    private void ChangeBackColor()
    {
        using var dialog = new ColorDialog { Color = _view.BackColor, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _view.BackColor = dialog.Color;
    }

    private void ChangePalette()
    {
        if (_image is null) return;
        using var dialog = new PaletteEditorForm(_image.Palette);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _image.SetPalette(dialog.Palette);
        _bitmap?.Dispose();
        _bitmap = _image.ToBitmap();
        _view.Image = _bitmap;
        _modified = true;
        UpdateStatus();
    }

    private void ApplyScale() => _view.ViewScale = _fit ? _view.FitScale : _zoom;

    private void UpdateStatus()
    {
        if (_image is null) return;
        _statusLabel.Text =
            $"{_image.Width}x{_image.Height}  depth={_image.Depth} ({1 << _image.Depth}色)  コメント: {_image.Comment}";
        _originLabel.Text = $"｜表示位置=({_image.OriginX},{_image.OriginY}){(_modified ? "  *未保存" : "")}";
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_modified) return true;
        DialogResult result = MessageBox.Show(this, "表示位置の変更が保存されていません。保存しますか？",
            "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.No) return true;
        Save(_path);
        return !_modified; // 保存に失敗した場合は続行しない
    }

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        string path = files[0];
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".bmp" or ".png" or ".mag") ImportImageFile(path);
        else OpenFile(path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bitmap?.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>パレットの各色を一覧表示し、選択した色を RGB スライダー（各32段階）で変更するダイアログ。</summary>
internal sealed class PaletteEditorForm : Form
{
    private readonly List<Button> _swatchButtons = new();
    private readonly TrackBar _redBar;
    private readonly TrackBar _greenBar;
    private readonly TrackBar _blueBar;
    private readonly Label _redValue;
    private readonly Label _greenValue;
    private readonly Label _blueValue;
    private int _selected;
    private bool _updating;

    public ushort[] Palette { get; }

    public PaletteEditorForm(ushort[] currentPalette)
    {
        Palette = (ushort[])currentPalette.Clone();

        const int cellWidth = 64;
        const int swatchRowHeight = 80; // ボタン40 + 余白（スライダーと重ならない高さ）
        int columns = Palette.Length; // 折り返さず横一列に並べる

        Text = "パレットを変更";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var swatches = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(8, 12, 8, 12),
        };
        for (int i = 0; i < Palette.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Width = 56,
                Height = 40,
                BackColor = MitImage.ToColor(Palette[i]),
                ForeColor = ReadableTextColor(MitImage.ToColor(Palette[i])),
                Text = index.ToString(),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(4),
            };
            button.Click += (_, _) => SelectIndex(index);
            _swatchButtons.Add(button);
            swatches.Controls.Add(button);
        }

        var sliders = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 8),
        };
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        (_redBar, _redValue) = AddSlider(sliders, 0, "R");
        (_greenBar, _greenValue) = AddSlider(sliders, 1, "G");
        (_blueBar, _blueValue) = AddSlider(sliders, 2, "B");

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        // 行0=スウォッチ（固定高）、行1=スライダー、行2=ボタン（自動サイズ）の3行構成。
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, swatchRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(swatches, 0, 0);
        root.Controls.Add(sliders, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        // スクロールなしで全スウォッチを表示するため、必要な幅をそのまま採用する（画面幅は超え得る）。
        int width = Math.Max(columns * cellWidth + 16, sliders.PreferredSize.Width);
        ClientSize = new Size(width,
            swatchRowHeight + sliders.PreferredSize.Height + buttons.PreferredSize.Height);

        SelectIndex(0);
    }

    private (TrackBar Bar, Label Value) AddSlider(TableLayoutPanel host, int row, string name)
    {
        var label = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
        var bar = new TrackBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 31, // X68000 パレットは各チャンネル5ビット＝32段階
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 4,
        };
        var value = new Label { Text = "0", AutoSize = true, Anchor = AnchorStyles.Left, Width = 28, Margin = new Padding(3, 8, 3, 3) };
        bar.ValueChanged += (_, _) => OnSliderChanged();
        host.Controls.Add(label, 0, row);
        host.Controls.Add(bar, 1, row);
        host.Controls.Add(value, 2, row);
        return (bar, value);
    }

    private void SelectIndex(int index)
    {
        _selected = index;
        for (int i = 0; i < _swatchButtons.Count; i++)
        {
            _swatchButtons[i].FlatAppearance.BorderSize = i == index ? 3 : 1;
            _swatchButtons[i].FlatAppearance.BorderColor = i == index ? Color.OrangeRed : Color.Gray;
        }

        _updating = true;
        ushort p = Palette[index];
        _greenBar.Value = (p >> 11) & 0x1f;
        _redBar.Value = (p >> 6) & 0x1f;
        _blueBar.Value = (p >> 1) & 0x1f;
        _updating = false;
        UpdateSelectedColor();
    }

    private void OnSliderChanged()
    {
        if (_updating) return;
        UpdateSelectedColor();
    }

    private void UpdateSelectedColor()
    {
        int intensity = Palette[_selected] & 1;
        Palette[_selected] = (ushort)((_greenBar.Value << 11) | (_redBar.Value << 6) | (_blueBar.Value << 1) | intensity);

        Color color = MitImage.ToColor(Palette[_selected]);
        Button button = _swatchButtons[_selected];
        button.BackColor = color;
        button.ForeColor = ReadableTextColor(color);

        _redValue.Text = _redBar.Value.ToString();
        _greenValue.Text = _greenBar.Value.ToString();
        _blueValue.Text = _blueBar.Value.ToString();
    }

    /// <summary>背景色の輝度に応じて読みやすい白／黒の文字色を返す。</summary>
    private static Color ReadableTextColor(Color background)
    {
        double luminance = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
        return luminance < 128 ? Color.White : Color.Black;
    }
}
