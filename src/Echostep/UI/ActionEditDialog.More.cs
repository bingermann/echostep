using System.Diagnostics;
using Echostep.Core;
using Echostep.Win;

namespace Echostep.UI;

/// <summary>Edit fields for the actions under More: Find image, Type text, Set variable, Run / open, Message / sound.</summary>
internal sealed partial class ActionEditDialog
{
    /// <summary>Variable names used elsewhere in the list, then the built-in ones, for the name boxes.</summary>
    private IEnumerable<string> VariableSuggestions() =>
        _all.OfType<SetVariableAction>().Where(a => a.Operation != VariableOperation.ToClipboard).Select(a => a.Name.Trim())
            .Concat(_all.OfType<IfAction>().Where(a => a.Condition == IfCondition.Variable && a.Variable is not null).Select(a => a.Variable!.Trim()))
            .Where(Variables.IsValidName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(Variables.BuiltIn.Where(b => b is not "date" and not "time" and not "clipboard"));

    private const string VariableHelp = "Write {name} to put in a variable's value, for example {count}. " +
                                        "Also available: {date}, {time}, {clipboard}, and {foundX}, {foundY} after Find image.";

    private void BuildMoreFields()
    {
        switch (_work)
        {
            case FindImageAction fi: FindImageFields(fi); break;

            case TypeTextAction tt:
            {
                _grid.Note("Types text into whatever has the keyboard focus. Any characters work, whatever the keyboard layout. " +
                           "A new line presses Enter. " + VariableHelp, 440);
                var text = new TextBox
                {
                    Text = tt.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, AcceptsReturn = true,
                    ScrollBars = ScrollBars.Vertical, Width = 380, Height = 110,
                };
                _grid.Add("Text", text);
                var delay = _grid.Int("Pause between characters (ms)", tt.CharDelayMs, 0, 10_000, 80);
                _tips.SetToolTip(delay, "0 types everything at once. Some programs and games need a short pause between characters.");
                _validators.Add(() => text.Text.Length == 0 ? "Enter the text to type." : null);
                _apply.Add(() =>
                {
                    tt.Text = text.Text.Replace("\r\n", "\n");
                    tt.CharDelayMs = delay.Value;
                });
                break;
            }

            case SetVariableAction sv:
            {
                _grid.Note("Variables hold text or numbers while the macro plays, for counting, remembering copied text and so on. " +
                           "They start empty each time Play starts. " + VariableHelp, 440);
                var op = _grid.Combo("Action", ["Set to a value", "Add a number", "Subtract a number", "Copy the clipboard into it", "Put a value on the clipboard"], null, false, 260);
                op.SelectedIndex = (int)sv.Operation;
                var name = _grid.Combo("Variable", VariableSuggestions(), sv.Name, editable: true, width: 180);
                var value = _grid.TextField("Value", sv.Value, 300);
                _tips.SetToolTip(name, "Letters, digits and _, not starting with a digit. Not case-sensitive.");
                void Sync()
                {
                    var o = (VariableOperation)op.SelectedIndex;
                    name.Enabled = o != VariableOperation.ToClipboard;
                    value.Enabled = o != VariableOperation.FromClipboard;
                }
                op.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                _validators.Add(() =>
                {
                    var o = (VariableOperation)op.SelectedIndex;
                    if (o != VariableOperation.ToClipboard && !Variables.IsValidName(name.Text))
                        return $"\"{name.Text}\" is not a variable name. Use letters, digits and _, not starting with a digit.";
                    if (o is VariableOperation.Add or VariableOperation.Subtract && !value.Text.Contains('{') && !Variables.TryNumber(value.Text, out _))
                        return "Enter a number to add or subtract, like 1 or 2.5.";
                    return null;
                });
                _apply.Add(() =>
                {
                    sv.Operation = (VariableOperation)op.SelectedIndex;
                    if (sv.Operation != VariableOperation.ToClipboard) sv.Name = name.Text.Trim();
                    sv.Value = sv.Operation == VariableOperation.FromClipboard ? null : value.Text;
                });
                break;
            }

            case RunAction run:
            {
                _grid.Note("Opens a program, document, folder or web address, the same way double-clicking it would. " + VariableHelp, 440);
                var target = new TextBox { Text = run.Target, Width = 300 };
                var browse = new RoundButton { Text = "Browse…", AutoSize = true };
                _grid.Row("Program, file or address", target, browse);
                var args = _grid.TextField("Arguments (optional)", run.Arguments, 300);
                var wait = _grid.Add("", new CheckBox { Text = "Wait until it closes before going on", AutoSize = true, Checked = run.Wait });
                _tips.SetToolTip(wait, "Useful for scripts and installers. Web addresses and documents opened in a program that is already running cannot be waited for.");
                var test = new RoundButton { Text = "Open now", AutoSize = true };
                var result = new Label { AutoSize = true, MaximumSize = new Size(300, 0) };
                _grid.Row("", test, result);
                browse.Click += (_, _) =>
                {
                    using var dlg = new OpenFileDialog { Filter = "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files|*.*" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
                };
                test.Click += (_, _) =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo(target.Text.Trim()) { UseShellExecute = true };
                        if (!string.IsNullOrWhiteSpace(args.Text)) psi.Arguments = args.Text;
                        Process.Start(psi)?.Dispose();
                        result.Text = "Opened.";
                    }
                    catch (Exception ex)
                    {
                        result.Text = ex.Message;
                    }
                };
                _validators.Add(() => string.IsNullOrWhiteSpace(target.Text) ? "Enter what to open." : null);
                _apply.Add(() =>
                {
                    run.Target = target.Text.Trim();
                    run.Arguments = NullIfBlank(args.Text);
                    run.Wait = wait.Checked;
                });
                break;
            }

            case MessageAction msg:
            {
                _grid.Note("Plays a sound, shows a message, or asks a Yes/No question. Playback waits until the message is answered.", 440);
                var box = _grid.Combo("Show", ["Nothing (sound only)", "A message with OK", "A Yes / No question"], null, false, 220);
                box.SelectedIndex = (int)msg.Box;
                var text = new TextBox { Text = msg.Text ?? "", Multiline = true, AcceptsReturn = true, Width = 380, Height = 70, ScrollBars = ScrollBars.Vertical };
                _grid.Add("Text", text);
                _tips.SetToolTip(text, VariableHelp);
                var sound = _grid.Combo("Sound", ["None", "Beep", "Information", "Warning", "Error"], null, false, 160);
                sound.SelectedIndex = (int)msg.Sound;
                var then = TargetField("Then go to", msg.Then, includeStop: true, allowEmpty: true, "Where to go next (for a question: when the answer is Yes).");
                var els = TargetField("If the answer is No, go to", msg.Else, includeStop: true, allowEmpty: true, "Where to go when the answer is No.");
                var test = new RoundButton { Text = "Try it", AutoSize = true };
                var result = new Label { AutoSize = true };
                _grid.Row("", test, result);
                void Show(Control c, bool on)
                {
                    c.Visible = on;
                    if (_grid.GetControlFromPosition(0, _grid.GetRow(c)) is Label caption && caption != c) caption.Visible = on;
                }
                void Sync()
                {
                    Show(text, box.SelectedIndex > 0);
                    Show(els, box.SelectedIndex == 2);
                    result.Text = "";
                }
                box.SelectedIndexChanged += (_, _) => Sync();
                Sync();
                test.Click += (_, _) =>
                {
                    var host = WinHost.Instance;
                    if (sound.SelectedIndex > 0) host.PlaySound((MessageSound)sound.SelectedIndex);
                    if (box.SelectedIndex == 0) return;
                    var yes = host.ShowMessage(text.Text, box.SelectedIndex == 2);
                    if (box.SelectedIndex == 2) result.Text = yes ? "Yes" : "No";
                };
                _validators.Add(() => box.SelectedIndex == 0 && sound.SelectedIndex == 0 ? "Choose a sound, or show a message." : null);
                _apply.Add(() =>
                {
                    msg.Box = (MessageBoxKind)box.SelectedIndex;
                    msg.Text = msg.Box == MessageBoxKind.None ? null : text.Text.Replace("\r\n", "\n");
                    msg.Sound = (MessageSound)sound.SelectedIndex;
                    msg.Then = Branch(then);
                    msg.Else = msg.Box == MessageBoxKind.YesNo ? Branch(els) : null;
                });
                break;
            }
        }
    }

    private void FindImageFields(FindImageAction fi)
    {
        _grid.Note("Looks for a picture on the screen, such as a button or an icon, even when it is not always in the same place. " +
                   "Capture it once, then the macro can wait for it, click it, or check whether it is there. " +
                   "Where it was found goes in {foundX} and {foundY}.", 440);
        var image = fi.Image;
        var preview = new PictureBox { Width = 200, Height = 90, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Bg, BorderStyle = BorderStyle.FixedSingle };
        var size = new Label { AutoSize = true, ForeColor = Theme.Muted };
        var capture = new RoundButton { Text = "Capture from screen…", AutoSize = true, Kind = ButtonKind.Primary };
        var load = new RoundButton { Text = "Load file…", AutoSize = true };
        _grid.Row("Image", preview, size);
        _grid.Row("", capture, load);
        _tips.SetToolTip(capture, "Dims the screen: drag a tight rectangle around the button or icon (at most 400 × 400 pixels).");
        void ShowImage()
        {
            preview.Image?.Dispose();
            preview.Image = null;
            if (ImagePattern.TryDecode(image, out var p))
            {
                preview.Image = ToBitmap(p!);
                preview.SizeMode = p!.Width <= preview.Width && p.Height <= preview.Height ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
                size.Text = $"{p.Width} × {p.Height}";
            }
            else size.Text = "No image yet";
        }
        ShowImage();
        bool Accept(ImagePattern p)
        {
            if (p.Width > ImagePattern.MaxSide || p.Height > ImagePattern.MaxSide)
            {
                MessageBox.Show(this, $"That is {p.Width} × {p.Height}. Pick a smaller part (at most {ImagePattern.MaxSide} × {ImagePattern.MaxSide}): a small, distinctive piece is found faster and more reliably.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            image = p.Encode();
            ShowImage();
            return true;
        }
        capture.Click += (_, _) =>
        {
            var restore = PixelUi.HideWindows(this);
            Thread.Sleep(150); // let the windows disappear before the screen is read
            var zone = ZoneSelectForm.Pick();
            ScreenImage? shot = zone is { Width: > 0, Height: > 0 } r ? Native.Capture(r.X, r.Y, r.Width, r.Height) : null;
            restore();
            if (shot is null) return;
            Accept(new ImagePattern(shot.Width, shot.Height, shot.Pixels));
        };
        load.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Images (*.png;*.bmp;*.jpg)|*.png;*.bmp;*.jpg;*.jpeg|All files|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var bmp = new Bitmap(dlg.FileName);
                Accept(FromBitmap(bmp));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
            {
                MessageBox.Show(this, "Could not read that image: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var where = _grid.Combo("Search", ["Entire screen", "Only in a zone (faster)"], null, false, 220);
        where.SelectedIndex = fi.EntireScreen ? 0 : 1;
        var zx = FieldGrid.NewInt(fi.X, -100_000, 100_000, 70);
        var zy = FieldGrid.NewInt(fi.Y, -100_000, 100_000, 70);
        var zw = FieldGrid.NewInt(fi.Width, 0, 100_000, 70);
        var zh = FieldGrid.NewInt(fi.Height, 0, 100_000, 70);
        var zoneRow = _grid.Row("Zone X, Y, W, H", zx, zy, zw, zh);
        var define = new RoundButton { Text = "Define zone…", AutoSize = true };
        var showZone = new RoundButton { Text = "Show zone", AutoSize = true };
        var zoneButtons = _grid.Row("", define, showZone);
        var tol = _grid.Int("Tolerance (0–255)", fi.Tolerance, 0, 255, 70);
        _tips.SetToolTip(tol, "How much each color channel may differ. 0 needs an exact match; 20–40 copes with smoothed text and slight color changes.");
        define.Click += (_, _) =>
        {
            var restore = PixelUi.HideWindows(this);
            var zone = ZoneSelectForm.Pick();
            restore();
            if (zone is not { } r) return;
            zx.Value = r.X; zy.Value = r.Y; zw.Value = r.Width; zh.Value = r.Height;
        };
        showZone.Click += (_, _) => { if (zw.Value > 0 && zh.Value > 0) ZoneFlash.Show(new Rectangle(zx.Value, zy.Value, zw.Value, zh.Value)); };

        _grid.Header("If it is found");
        var mouse = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        mouse.Items.AddRange(["No mouse action", "Move to it", "Left-click it", "Right-click it", "Middle-click it", "Double-click it"]);
        mouse.SelectedIndex = (int)fi.Mouse;
        var anchor = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        anchor.Items.AddRange(["Centered", "Top-left", "Top-right", "Bottom-left", "Bottom-right"]);
        anchor.SelectedIndex = (int)fi.Anchor;
        _grid.Row("Mouse action", mouse, new Label { Text = "at", AutoSize = true }, anchor);
        var then = TargetField("Go to", fi.Then, includeStop: true, allowEmpty: true, "Where to go once the image is found.");

        _grid.Header("If it is not found");
        var mode = _grid.Combo("Then", ["Keep looking (with a timeout)", "Check only once"], null, false, 240);
        mode.SelectedIndex = fi.Once ? 1 : 0;
        var timeoutField = _grid.Duration("Keep looking for", fi.TimeoutMs);
        timeoutField.SetTip(_tips, "How long to keep looking before giving up. 0 looks forever (until Stop).");
        _validators.Add(() => mode.SelectedIndex == 0 ? timeoutField.Problem("how long to keep looking") : null);
        var timeout = new TimeoutValue(timeoutField);
        var notFound = TargetField("…and then go to", fi.Once ? (string.IsNullOrWhiteSpace(fi.OnTimeout) ? Labels.Next : fi.OnTimeout) : fi.OnTimeout,
            includeStop: true, allowEmpty: true, "Where to go when the image is not there. With Check only once, next continues with the following row.");

        var test = new RoundButton { Text = "Test now", AutoSize = true };
        var result = new Label { AutoSize = true, MaximumSize = new Size(320, 0) };
        _grid.Row("", test, result);
        test.Click += (_, _) =>
        {
            if (!ImagePattern.TryDecode(image, out var p)) { result.Text = "Capture an image first."; return; }
            var restore = PixelUi.HideWindows(this);
            Thread.Sleep(150);
            var sw = Stopwatch.StartNew();
            var zone = where.SelectedIndex == 1 && zw.Value > 0 && zh.Value > 0;
            var frame = zone ? Native.Capture(zx.Value, zy.Value, zw.Value, zh.Value) : Native.Capture(0, 0, 0, 0);
            var at = frame is null ? null : ImageSearch.Find(frame, p!, tol.Value);
            sw.Stop();
            restore();
            if (at is { } found)
            {
                result.Text = $"Found at {found.X}, {found.Y} (searched in {sw.ElapsedMilliseconds} ms).";
                ZoneFlash.Show(new Rectangle(found.X, found.Y, p!.Width, p.Height));
            }
            else result.Text = frame is null ? "Could not read the screen." : $"Not found right now (searched in {sw.ElapsedMilliseconds} ms). Try more tolerance?";
        };

        void Sync()
        {
            zoneRow.Enabled = zoneButtons.Enabled = where.SelectedIndex == 1;
            anchor.Enabled = mouse.SelectedIndex > 0;
            timeoutField.Enabled = mode.SelectedIndex == 0;
        }
        where.SelectedIndexChanged += (_, _) => Sync();
        mouse.SelectedIndexChanged += (_, _) => Sync();
        mode.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        _validators.Add(() => ImagePattern.TryDecode(image, out _) ? null : "Capture the image to look for (or load it from a file).");
        _validators.Add(() => where.SelectedIndex == 1 && (zw.Value <= 0 || zh.Value <= 0)
            ? "Define the zone to search (its width and height must be more than 0), or choose Entire screen." : null);
        _apply.Add(() =>
        {
            fi.Image = image;
            var z = where.SelectedIndex == 1;
            fi.X = z ? zx.Value : 0; fi.Y = z ? zy.Value : 0; fi.Width = z ? zw.Value : 0; fi.Height = z ? zh.Value : 0;
            fi.Tolerance = tol.Value;
            fi.Mouse = (ChangeMouseAction)mouse.SelectedIndex;
            fi.Anchor = (ChangeAnchor)anchor.SelectedIndex;
            fi.Then = Branch(then);
            fi.Once = mode.SelectedIndex == 1;
            fi.TimeoutMs = timeout.Value;
            fi.OnTimeout = fi.Once ? Branch(notFound) : string.IsNullOrWhiteSpace(notFound.Text) ? Labels.Stop : notFound.Text.Trim();
        });
    }

    internal static Bitmap ToBitmap(ImagePattern p)
    {
        var bmp = new Bitmap(p.Width, p.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, p.Width, p.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var argb = new int[p.Pixels.Length];
        for (var i = 0; i < argb.Length; i++) argb[i] = p.Pixels[i] | unchecked((int)0xFF000000);
        for (var row = 0; row < p.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(argb, row * p.Width, data.Scan0 + row * data.Stride, p.Width);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static ImagePattern FromBitmap(Bitmap bmp)
    {
        using var copy = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, copy.PixelFormat);
        var px = new int[copy.Width * copy.Height];
        for (var row = 0; row < copy.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, px, row * copy.Width, copy.Width);
        copy.UnlockBits(data);
        for (var i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
        return new ImagePattern(copy.Width, copy.Height, px);
    }
}
