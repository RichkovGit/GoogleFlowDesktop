using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GoogleFlowDesktop
{
    /// <summary>
    /// The Prompt Studio, rebuilt with native WinForms controls.
    /// No WebView2, no HTML, no local HTTP server — just real Windows controls.
    /// </summary>
    public class NativeStudio : UserControl
    {
        private TableLayoutPanel root;

        private InputHost promptHost;
        private TextBox promptBox;
        private Label charCount;
        private InputHost negativeHost;

        private FlowLayoutPanel ratioPanel;
        private FlowLayoutPanel presetPanel;
        private InputHost compiledHost;

        private ToggleSwitch chkRussian;
        private ToggleSwitch chkLikeness;
        private ToggleSwitch chkClutter;
        private ToggleSwitch chkWrapper;
        private ToggleSwitch chkNegative;

        private readonly HashSet<string> activeChips = new HashSet<string>();
        private string activePresetId;
        private string activePresetSuffix = "";
        private string selectedRatio = "16:9";

        public Action<string> SendToFlowRequested;
        public Action<string> StatusChanged;

        public NativeStudio()
        {
            BackColor = Theme.BgBase;
            Dock = DockStyle.Fill;
            BuildUi();
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
            root.BackColor = Theme.BgBase;
            root.Padding = new Padding(18, 16, 18, 20);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 0;
            Controls.Add(root);

            BuildPromptCard();
            BuildPresetCard();
            BuildTagCard();
            BuildFilterCard();
            BuildNegativeCard();
            BuildCompiledCard();
        }

        private void AddCard(CardLayout card)
        {
            int row = root.RowCount;
            root.RowCount = row + 1;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(card, 0, row);
        }

        private void BuildPromptCard()
        {
            CardLayout card = new CardLayout();

            FlatButton btnAi = Ui.Button("🪄 Улучшить с ИИ", ButtonKind.Chip, 128);
            btnAi.Height = 26;
            btnAi.Click += (s, e) => AiOptimize();

            FlatButton btnClear = Ui.Button("Очистить", ButtonKind.Chip, 80);
            btnClear.Height = 26;
            btnClear.Click += (s, e) => ClearAll();

            card.AddBlock(Ui.HeaderRow("Описание сцены", "базовый промпт", btnAi, btnClear));

            promptHost = new InputHost();
            promptHost.MakeMultiline(4, true);
            promptHost.Height = 108;
            promptHost.SetCue("Опишите сцену, персонажа, действие или атмосферу для Google Flow...");
            promptBox = promptHost.Inner;
            promptBox.Text = "Киберпанк девушка на мотоцикле под неоновым дождём в ночном городе будущего";
            promptBox.TextChanged += (s, e) => { RefreshCharCount(); Recompute(); };
            card.AddBlock(promptHost);

            TableLayoutPanel meta = new TableLayoutPanel();
            meta.ColumnCount = 2;
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            meta.AutoSize = true;
            meta.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            meta.Dock = DockStyle.Top;
            meta.Margin = new Padding(0, 8, 0, 2);
            meta.BackColor = Theme.BgCard;

            charCount = Ui.Hint("0 симв.");
            FlatButton btnCopyDirect = Ui.Button("📋 Копировать текст", ButtonKind.Chip, 142);
            btnCopyDirect.Height = 24;
            btnCopyDirect.Click += (s, e) =>
            {
                ClipboardUtil.Set(promptBox.Text);
                Toast("📋 Базовый промпт скопирован");
            };
            meta.Controls.Add(charCount, 0, 0);
            meta.Controls.Add(btnCopyDirect, 1, 0);
            card.AddBlock(meta);

            card.AddBlock(Ui.Section("Соотношение сторон"));

            ratioPanel = Ui.WrapPanel();
            for (int i = 0; i < PromptKit.AspectRatios.Length; i++)
            {
                string ratio = PromptKit.AspectRatios[i];
                FlatButton b = Ui.Chip(PromptKit.AspectRatioLabels[i]);
                b.Tag = ratio;
                b.Selected = (ratio == selectedRatio);
                b.Click += RatioClicked;
                ratioPanel.Controls.Add(b);
            }
            card.AddBlock(ratioPanel);

            AddCard(card);
        }

        private void BuildPresetCard()
        {
            CardLayout card = new CardLayout();
            card.AddBlock(Ui.HeaderRow("1-Клик пресеты", "свет, оптика, стиль"));

            presetPanel = Ui.WrapPanel();
            foreach (PresetItem p in PromptKit.Presets)
            {
                FlatButton b = new FlatButton();
                b.Kind = ButtonKind.Ghost;
                b.Text = p.Name + "\n" + p.Description;
                b.Font = Theme.F8;
                b.Width = 196;
                b.Height = 50;
                b.Margin = new Padding(0, 0, 8, 8);
                b.Tag = p;
                b.Click += PresetClicked;
                presetPanel.Controls.Add(b);
            }
            card.AddBlock(presetPanel);
            AddCard(card);
        }

        private void BuildTagCard()
        {
            CardLayout card = new CardLayout();
            card.AddBlock(Ui.HeaderRow("Справочник тегов", "клик, чтобы добавить"));

            AddTagSection(card, "📷 Оптика и ракурсы", PromptKit.Optics);
            AddTagSection(card, "💡 Стили освещения", PromptKit.Lighting);
            AddTagSection(card, "🎨 Художественные стили", PromptKit.ArtStyles);
            AddTagSection(card, "🎬 Динамика камеры и видео", PromptKit.VideoDynamics);

            AddCard(card);
        }

        private void AddTagSection(CardLayout card, string title, List<TagItem> items)
        {
            card.AddBlock(Ui.Section(title));
            FlowLayoutPanel panel = Ui.WrapPanel();
            foreach (TagItem t in items)
            {
                FlatButton chip = Ui.Chip(t.Label);
                chip.Tag = t;
                chip.Click += TagClicked;
                panel.Controls.Add(chip);
            }
            card.AddBlock(panel);
        }

        private void BuildFilterCard()
        {
            CardLayout card = new CardLayout();
            card.AddBlock(Ui.HeaderRow("Защитные фильтры и директивы", null));

            chkRussian = AddToggle(card, "Строгий русский язык — текст и диалоги только по-русски", true);
            chkLikeness = AddToggle(card, "Портретное сходство — сохранять лицо, причёску и одежду", true);
            chkClutter = AddToggle(card, "Zero Clutter — запрет лишних людей и предметов", true);
            chkWrapper = AddToggle(card, "Prompt Wrapper — рамка Masterpiece 8K photorealism", true);

            AddCard(card);
        }

        private ToggleSwitch AddToggle(CardLayout card, string text, bool initial)
        {
            ToggleSwitch t = new ToggleSwitch();
            t.Text = text;
            t.Checked = initial;
            t.Height = 28;
            t.Margin = new Padding(0, 2, 0, 4);
            t.CheckedChanged += (s, e) => Recompute();
            card.AddBlock(t);
            return t;
        }

        private void BuildNegativeCard()
        {
            CardLayout card = new CardLayout();

            chkNegative = new ToggleSwitch();
            chkNegative.Text = "Вкл";
            chkNegative.Checked = true;
            chkNegative.Width = 96;
            chkNegative.Height = 26;
            chkNegative.CheckedChanged += (s, e) =>
            {
                negativeHost.Inner.Enabled = chkNegative.Checked;
                negativeHost.Enabled = chkNegative.Checked;
                Recompute();
            };

            card.AddBlock(Ui.HeaderRow("Универсальный негативный фильтр", null, chkNegative));

            negativeHost = new InputHost();
            negativeHost.MakeMultiline(3, true);
            negativeHost.Height = 84;
            negativeHost.Inner.Text = PromptKit.DefaultNegativePrompt;
            card.AddBlock(negativeHost);

            card.AddBlock(Ui.Hint("Отсекает деформации, артефакты, водяные знаки и размытие."));
            AddCard(card);
        }

        private void BuildCompiledCard()
        {
            CardLayout card = new CardLayout();

            FlatButton btnCopy = Ui.Button("📋 Скопировать", ButtonKind.Chip, 130);
            btnCopy.Height = 26;
            btnCopy.Click += (s, e) => CopyCompiled();
            card.AddBlock(Ui.HeaderRow("Итоговый технический промпт", null, btnCopy));

            compiledHost = new InputHost();
            compiledHost.MakeMultiline(5, true);
            compiledHost.Inner.Font = Theme.Mono;
            compiledHost.Inner.ForeColor = Color.FromArgb(198, 226, 240);
            compiledHost.SetReadOnly(true);
            compiledHost.Height = 132;
            card.AddBlock(compiledHost);

            FlatButton send = Ui.Button("🚀 Вставить в Google Flow Canvas", ButtonKind.Accent, 0);
            send.Height = 42;
            send.Dock = DockStyle.Top;
            send.Margin = new Padding(0, 4, 0, 0);
            send.Click += (s, e) => SendToFlow();
            card.AddBlock(send);

            AddCard(card);
        }

        // ------------------------------------------------------------------
        // Behaviour
        // ------------------------------------------------------------------
        private void RatioClicked(object sender, EventArgs e)
        {
            FlatButton b = (FlatButton)sender;
            selectedRatio = (string)b.Tag;
            foreach (Control c in ratioPanel.Controls)
            {
                FlatButton other = c as FlatButton;
                if (other != null) other.Selected = (other == b);
            }
            Recompute();
        }

        private void PresetClicked(object sender, EventArgs e)
        {
            FlatButton b = (FlatButton)sender;
            PresetItem p = (PresetItem)b.Tag;
            bool wasSelected = b.Selected;
            foreach (Control c in presetPanel.Controls)
            {
                FlatButton other = c as FlatButton;
                if (other != null) other.Selected = false;
            }
            if (wasSelected)
            {
                activePresetId = null;
                activePresetSuffix = "";
            }
            else
            {
                b.Selected = true;
                activePresetId = p.Id;
                activePresetSuffix = p.Suffix;
            }
            Recompute();
        }

        private void TagClicked(object sender, EventArgs e)
        {
            FlatButton chip = (FlatButton)sender;
            TagItem t = (TagItem)chip.Tag;
            string current = promptBox.Text == null ? "" : promptBox.Text.Trim();

            if (chip.Selected)
            {
                chip.Selected = false;
                activeChips.Remove(t.Value);
                current = RemoveTag(current, t.Value);
            }
            else
            {
                chip.Selected = true;
                activeChips.Add(t.Value);
                if (current.Length > 0)
                {
                    current += current.EndsWith(",") ? " " : ", ";
                }
                current += t.Value;
            }
            promptBox.Text = current;
            promptBox.SelectionStart = promptBox.TextLength;
        }

        private static string RemoveTag(string text, string tag)
        {
            string[] parts = text.Split(',');
            List<string> keep = new List<string>();
            foreach (string p in parts)
            {
                if (p.Trim() != tag) keep.Add(p.Trim());
            }
            string result = string.Join(", ", keep.ToArray());
            return result.Trim().Trim(',', ' ');
        }

        private void AiOptimize()
        {
            string cur = promptBox.Text.Trim();
            if (cur.Length == 0) cur = "Кинематографичная сцена в городе будущего";
            Random rnd = new Random();
            string pick = PromptKit.AiEnrichments[rnd.Next(PromptKit.AiEnrichments.Length)];
            promptBox.Text = cur + ", " + pick;
            promptBox.SelectionStart = promptBox.TextLength;
            Toast("🪄 Промпт обогащён деталями");
        }

        private void ClearAll()
        {
            promptBox.Text = "";
            activeChips.Clear();
            activePresetId = null;
            activePresetSuffix = "";
            foreach (Control c in presetPanel.Controls)
            {
                FlatButton b = c as FlatButton;
                if (b != null) b.Selected = false;
            }
            foreach (FlatButton c in AllChips())
            {
                c.Selected = false;
            }
        }

        private IEnumerable<FlatButton> AllChips()
        {
            foreach (Control cardCtl in root.Controls)
            {
                CardLayout card = cardCtl as CardLayout;
                if (card == null) continue;
                foreach (Control block in card.Controls)
                {
                    FlowLayoutPanel fp = block as FlowLayoutPanel;
                    if (fp == null) continue;
                    foreach (Control c in fp.Controls)
                    {
                        FlatButton b = c as FlatButton;
                        if (b != null && b.Kind == ButtonKind.Chip && b.Tag is TagItem) yield return b;
                    }
                }
            }
        }

        private void RefreshCharCount()
        {
            if (charCount != null) charCount.Text = promptBox.TextLength + " симв.";
        }

        /// <summary>Recomputes the compiled prompt preview.</summary>
        public void Recompute()
        {
            string compiled = CompileNow();
            if (compiledHost != null) compiledHost.Text = compiled;
            if (StatusChanged != null) StatusChanged(compiled.Length + " симв. в итоговом промпте");
        }

        public string CompiledPrompt
        {
            get { return CompileNow(); }
        }

        public string NegativePrompt
        {
            get { return chkNegative != null && chkNegative.Checked ? negativeHost.Text : ""; }
        }

        public string AspectRatio
        {
            get { return selectedRatio; }
        }

        public string BasePrompt
        {
            get { return promptBox.Text.Trim(); }
        }

        private string CompileNow()
        {
            return PromptKit.Compile(
                promptBox.Text,
                activePresetSuffix,
                chkWrapper == null || chkWrapper.Checked,
                chkLikeness == null || chkLikeness.Checked,
                chkClutter == null || chkClutter.Checked,
                chkRussian == null || chkRussian.Checked);
        }

        private void CopyCompiled()
        {
            ClipboardUtil.Set(CompileNow());
            Toast("📋 Итоговый промпт скопирован");
        }

        private void SendToFlow()
        {
            string text = CompileNow();
            ClipboardUtil.Set(text);
            if (SendToFlowRequested != null) SendToFlowRequested(text);
        }

        private void Toast(string msg)
        {
            if (StatusChanged != null) StatusChanged(msg);
        }
    }
}
