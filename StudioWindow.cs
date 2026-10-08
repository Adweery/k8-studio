// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

internal sealed class StudioWindow
{
    internal readonly Window Window;
    internal readonly StudioSettings Settings;
    internal LightState Current;
    readonly List<Border> keys=new List<Border>();
    readonly List<TextBlock> legends=new List<TextBlock>();
    DropShadowEffect keyboardGlow;
    readonly DispatcherTimer debounce=new DispatcherTimer();
    readonly DispatcherTimer probeTimer=new DispatcherTimer();
    readonly DispatcherTimer arrivalTimer=new DispatcherTimer();
    bool updating, busy, scanning, connected, closed, invalidHex, dirty, arrivalPending, restoreOnLogin;
    int revision;
    IntPtr notification;
    HwndSource hwnd;
    readonly bool previewOnly;
    internal string DialogCapturePath;
    readonly string[] effects={"Static","Light up on keypress","Fade on keypress","Sparkle","Rain","Random colors","Breathing","Color cycle","Circular gradient","Vertical gradient","Rainbow wave","Edge lighting","Horizontal reactive lines","Diagonal reactive lines","Reactive ripples","Sequence","Wave","Diagonal lines","Back and forth"};
    T Get<T>(string name) where T:class { return Window.FindName(name) as T; }
    static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    internal StudioWindow(bool preview)
    {
        previewOnly=preview;
        using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window=(Window)XamlReader.Load(stream);
        Window.Width=Math.Min(Window.Width,SystemParameters.WorkArea.Width-24);
        Window.Height=Math.Min(Window.Height,SystemParameters.WorkArea.Height-24);
        Window.MinWidth=Math.Min(Window.MinWidth,Window.Width); Window.MinHeight=Math.Min(Window.MinHeight,Window.Height);
        try { Settings=Storage.Load(); }
        catch(Exception ex) { Settings=new StudioSettings(); Get<TextBlock>("Status").Text="Could not load saved settings."; Get<TextBlock>("StatusDetail").Text=ex.Message; }
        Current=Settings.Last.Clone();
        Window.Icon=Icon();
        System.Windows.Shell.WindowChrome.SetWindowChrome(Window,new System.Windows.Shell.WindowChrome { CaptionHeight=0, ResizeBorderThickness=new Thickness(6), GlassFrameThickness=new Thickness(0), CornerRadius=new CornerRadius(0) });
        Get<Grid>("TitleBar").MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e)
        { if(e.ClickCount==2) ToggleMaximize(); else if(e.ButtonState==MouseButtonState.Pressed) Window.DragMove(); };
        Get<Button>("Close").Click+=delegate { Window.Close(); };
        Get<Button>("Minimize").Click+=delegate { Window.WindowState=WindowState.Minimized; };
        Get<Button>("Maximize").Click+=delegate { ToggleMaximize(); };
        Window.StateChanged+=delegate { Get<Button>("Maximize").Content=Window.WindowState==WindowState.Maximized?"❐":"□"; };
        Get<ComboBox>("Effect").ItemsSource=effects;
        BuildKeyboard(); BuildSwatches(); BuildPresets(); RefreshProfiles();
        UpdateUi(); WireControls();
        debounce.Interval=TimeSpan.FromMilliseconds(650);
        debounce.Tick+=async delegate { debounce.Stop(); if(Get<CheckBox>("Live").IsChecked==true && dirty && !invalidHex) await Apply(); };
        probeTimer.Interval=TimeSpan.FromSeconds(4);
        probeTimer.Tick+=async delegate { await Probe(); };
        arrivalTimer.Interval=TimeSpan.FromMilliseconds(700);
        arrivalTimer.Tick+=async delegate { arrivalTimer.Stop(); await Probe(); };
        Window.SourceInitialized+=delegate { RegisterNotifications(); };
        Window.Loaded+=async delegate
        {
            if(!previewOnly) { await Probe(); probeTimer.Start(); }
        };
        Window.Closed+=delegate
        {
            closed=true; debounce.Stop(); probeTimer.Stop(); arrivalTimer.Stop();
            if(notification!=IntPtr.Zero) UnregisterDeviceNotification(notification);
            if(hwnd!=null) hwnd.RemoveHook(DeviceMessage);
        };
        if(previewOnly) SetConnection(true);
    }
    void ToggleMaximize() { Window.WindowState=Window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized; }
    static ImageSource Icon()
    {
        DrawingGroup group=new DrawingGroup();
        using(DrawingContext dc=group.Open())
        {
            dc.DrawRoundedRectangle(Brush("#C4B5FD"),null,new Rect(0,0,64,64),15,15);
            FormattedText text=new FormattedText("K8",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),30,Brush("#201B32"),1.0);
            dc.DrawText(text,new Point(10,11));
        }
        return new DrawingImage(group);
    }
    void WireControls()
    {
        foreach(string name in new string[]{"Red","Green","Blue"}) Get<Slider>(name).ValueChanged+=delegate
        {
            if(updating) return;
            Current.Hex=String.Format("{0:X2}{1:X2}{2:X2}",(int)Get<Slider>("Red").Value,(int)Get<Slider>("Green").Value,(int)Get<Slider>("Blue").Value);
            invalidHex=false; UpdateUi(); Changed();
        };
        Get<TextBox>("Hex").TextChanged+=delegate
        {
            if(updating) return;
            string value=Get<TextBox>("Hex").Text.Trim().TrimStart('#'); uint number;
            invalidHex=value.Length!=6 || !UInt32.TryParse(value,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out number);
            Get<TextBox>("Hex").BorderBrush=Brush(invalidHex?"#FA8C9F":"#333D50");
            if(!invalidHex) { Current.Hex=value.ToUpperInvariant(); UpdateUi(false); Changed(); }
            SetButtons();
        };
        Get<Slider>("Brightness").ValueChanged+=delegate { if(updating)return; Current.Brightness=(int)Get<Slider>("Brightness").Value; UpdateUi(); Changed(); };
        Get<Slider>("Speed").ValueChanged+=delegate { if(updating)return; Current.Speed=(int)Get<Slider>("Speed").Value; UpdateUi(); Changed(); };
        Get<ComboBox>("Effect").SelectionChanged+=delegate { if(updating)return; Current.Effect=Get<ComboBox>("Effect").SelectedIndex+1; if(Current.Effect==10) Current.Direction=2; else Current.Direction=0; UpdateUi(); Changed(); };
        Get<ComboBox>("Direction").SelectionChanged+=delegate { if(updating || Get<ComboBox>("Direction").SelectedIndex<0)return; Current.Direction=Get<ComboBox>("Direction").SelectedIndex+(Current.Effect==10?2:0); Changed(); };
        Get<CheckBox>("Rainbow").Checked+=delegate { if(updating)return; Current.Rainbow=true; UpdateUi(); Changed(); };
        Get<CheckBox>("Rainbow").Unchecked+=delegate { if(updating)return; Current.Rainbow=false; UpdateUi(); Changed(); };
        Get<CheckBox>("Live").Checked+=delegate { if(dirty) Schedule(); };
        Get<CheckBox>("Live").Unchecked+=delegate { debounce.Stop(); };
        Get<Button>("Apply").Click+=async delegate { await Apply(); };
        Get<Button>("White").Click+=async delegate { Current=new LightState(); invalidHex=false; UpdateUi(); Changed(); await Apply(); };
        Get<Button>("Power").Click+=async delegate { Current.Power=!Current.Power; UpdateUi(); Changed(); await Apply(); };
        Get<Button>("SaveProfile").Click+=delegate { SaveProfile(); };
        Get<Button>("Settings").Click+=delegate { ShowSettings(); };
    }
    void Changed()
    {
        revision++; dirty=true;
        Status("Ready to apply","",false);
        Schedule();
    }
    void Schedule()
    {
        debounce.Stop(); if(!previewOnly && Get<CheckBox>("Live").IsChecked==true && !invalidHex && dirty) debounce.Start();
    }
    void Status(string title,string detail,bool error)
    {
        if(closed)return;
        Get<TextBlock>("Status").Text=title; Get<TextBlock>("Status").Foreground=Brush(error?"#FA8C9F":"#EDF0F7");
        Get<TextBlock>("StatusDetail").Text=error?detail:"";
        Get<TextBlock>("StatusDetail").Visibility=error?Visibility.Visible:Visibility.Collapsed;
    }
    void SetButtons()
    {
        Get<Button>("Apply").IsEnabled=connected && !busy && !invalidHex;
        Get<Button>("White").IsEnabled=connected && !busy;
        Get<Button>("Power").IsEnabled=connected && !busy;
    }
    void SetConnection(bool value)
    {
        connected=value;
        Get<TextBlock>("Connection").Text=value?"USB connected":"USB disconnected";
        Get<TextBlock>("Connection").Foreground=Brush(value?"#8DE0C4":"#E5AF83");
        Get<System.Windows.Shapes.Ellipse>("ConnectionDot").Fill=Brush(value?"#8DE0C4":"#E5AF83");
        SetButtons();
    }
    internal async Task Probe()
    {
        if(scanning || busy || closed || previewOnly)return;
        scanning=true; bool found=false; string failure=null;
        try { await Task.Run(delegate { using(Keyboard k=Keyboard.Find())found=true; }); }
        catch(Exception ex) { failure=ex.Message; }
        finally { scanning=false; }
        if(closed)return;
        bool returned=!connected && found;
        SetConnection(found);
        if(!found) { arrivalPending=true; Status("Connect USB · Cable mode",failure ?? "",false); return; }
        if((Settings.Reconnect || restoreOnLogin) && arrivalPending && found)
        {
            arrivalPending=false; restoreOnLogin=false;
            await Apply(Settings.Last.Clone(),false);
        }
        else if(returned) Status("","",false);
    }
    internal async Task Apply(LightState requested=null,bool fromUi=true)
    {
        if(busy || closed || previewOnly || (fromUi && invalidHex))return;
        debounce.Stop(); busy=true; SetButtons();
        LightState snapshot=requested ?? Current.Clone(); int version=revision;
        bool sent=false;
        Status("Applying…","",false);
        try
        {
            await Task.Run(delegate { StudioLighting.Apply(snapshot); }); sent=true;
            Settings.Last=snapshot.Clone();
            try { Storage.Save(Settings); }
            catch(Exception ex) { Status("Lighting applied; settings could not be saved.",ex.Message,true); return; }
            if(fromUi && revision==version)dirty=false;
            Status(snapshot.Power?"Applied":"Off","",false);
            if(!fromUi && dirty) Status("Restored · preview edits kept","",false);
        }
        catch(Exception ex) { Status("Error",ex.Message,true); }
        finally { busy=false; if(!closed) { SetButtons(); if(sent && dirty && fromUi) Schedule(); } }
    }
    void UpdateUi(bool replaceHex=true)
    {
        updating=true;
        try
        {
            uint rgb=Convert.ToUInt32(Current.Hex,16);
            Get<Slider>("Red").Value=(byte)(rgb>>16); Get<Slider>("Green").Value=(byte)(rgb>>8); Get<Slider>("Blue").Value=(byte)rgb;
            Get<TextBlock>("RedValue").Text=((byte)(rgb>>16)).ToString(); Get<TextBlock>("GreenValue").Text=((byte)(rgb>>8)).ToString(); Get<TextBlock>("BlueValue").Text=((byte)rgb).ToString();
            if(replaceHex) { Get<TextBox>("Hex").Text="#"+Current.Hex; Get<TextBox>("Hex").BorderBrush=Brush("#333D50"); invalidHex=false; }
            Get<Slider>("Brightness").Value=Current.Brightness; Get<TextBlock>("BrightnessValue").Text=Math.Round(Current.Brightness*100.0/15)+" %";
            Get<Slider>("Speed").Value=Current.Speed; Get<TextBlock>("SpeedValue").Text=(Current.Speed+1)+" / 16";
            Get<ComboBox>("Effect").SelectedIndex=Current.Effect-1;
            Get<Slider>("Speed").IsEnabled=Current.Effect!=1;
            Get<Grid>("SpeedRow").Visibility=Current.Effect==1?Visibility.Collapsed:Visibility.Visible;
            Get<Slider>("Speed").Visibility=Current.Effect==1?Visibility.Collapsed:Visibility.Visible;
            Get<CheckBox>("Rainbow").IsChecked=Current.Rainbow;
            bool fullColor=Current.Rainbow || Current.Effect==6 || Current.Effect==8;
            Get<CheckBox>("Rainbow").IsEnabled=Current.Effect!=6 && Current.Effect!=8;
            Get<Grid>("RgbControls").IsEnabled=!fullColor; Get<TextBox>("Hex").IsEnabled=!fullColor;
            Get<ComboBox>("Direction").ItemsSource=Current.Effect==10?new string[]{"Upwards","Downwards"}:new string[]{"Right","Left"};
            Get<ComboBox>("Direction").SelectedIndex=Current.Effect==10?Math.Max(0,Current.Direction-2):Math.Min(1,Current.Direction);
            Get<ComboBox>("Direction").IsEnabled=Current.Effect==10 || Current.Effect==11;
            Get<ComboBox>("Direction").Visibility=Current.Effect==10 || Current.Effect==11?Visibility.Visible:Visibility.Collapsed;
            Get<TextBlock>("EffectDescription").Text=Current.Effect==1?"One color. No animation.":Current.Effect==6 || Current.Effect==8?"Colors are set by the effect.":Current.Effect==2 || Current.Effect==3 || (Current.Effect>=13 && Current.Effect<=15)?"Responds to keypresses.":"Animation runs on the keyboard.";
            Get<TextBlock>("PreviewMode").Text=Current.Power?effects[Current.Effect-1]:"Lighting off";
            Get<Button>("Power").Content=Current.Power?"●  On":"○  Off";
            PaintKeyboard(fullColor); SetButtons();
        }
        finally { updating=false; }
    }
    void BuildSwatches()
    {
        foreach(string hex in new string[]{"FFFFFF","C4B5FD","60A5FA","45DFBE","FFAA76","FF719A"})
        {
            string color=hex;
            Button button=new Button { Width=32,Height=32,Margin=new Thickness(0,0,6,5),Padding=new Thickness(0),Background=Brush("#"+hex),BorderThickness=new Thickness(0),ToolTip="#"+hex };
            button.Click+=delegate { Current.Hex=color; Current.Rainbow=false; if(Current.Effect==6 || Current.Effect==8)Current.Effect=1; UpdateUi(); Changed(); };
            Get<WrapPanel>("Swatches").Children.Add(button);
        }
    }
    void BuildPresets()
    {
        string[] names={"Pure white","Lavender","Ocean","Golden hour"};
        string[] colors={"FFFFFF","B399FF","43D9CF","FFB376"};
        for(int i=0;i<names.Length;i++)
        {
            string color=colors[i];
            StackPanel content=new StackPanel { Orientation=Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width=8,Height=8,Fill=Brush("#"+color),Margin=new Thickness(0,0,10,0) });
            content.Children.Add(new TextBlock { Text=names[i],FontSize=16,VerticalAlignment=VerticalAlignment.Center });
            Button button=new Button { Content=content,Margin=new Thickness(0,0,0,6),Padding=new Thickness(12,11,12,11),HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brush("#151C29"),BorderThickness=new Thickness(0) };
            button.Click+=delegate { Current=new LightState { Hex=color }; UpdateUi(); Changed(); };
            Get<StackPanel>("Presets").Children.Add(button);
        }
    }
    void BuildKeyboard()
    {
        Canvas canvas=Get<Canvas>("KeyboardPreview");
        keyboardGlow=new DropShadowEffect { BlurRadius=25,ShadowDepth=2,Opacity=.24,Color=Colors.White };
        Border caseBorder=new Border { Width=750,Height=258,Background=new LinearGradientBrush(Color.FromRgb(48,57,72),Color.FromRgb(23,30,42),90),BorderBrush=Brush("#465164"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Effect=keyboardGlow };
        caseBorder.Child=new Border { Margin=new Thickness(4,3,4,5),Background=Brush("#0B1018"),BorderBrush=Brush("#070B11"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8) };
        Canvas.SetLeft(caseBorder,5); Canvas.SetTop(caseBorder,2); canvas.Children.Add(caseBorder);
        // ANSI TKL: 87 keycaps, grouped function keys and separate navigation cluster.
        double u=38, y=13;
        AddKey("Esc",17,y,u); double x=17+u*1.65;
        for(int i=1;i<=12;i++) { AddKey("F"+i,x,y,u); x+=u; if(i==4 || i==8)x+=u*.38; }
        AddKey("Prt",624,y,u); AddKey("Scr",662,y,u); AddKey("☼",700,y,u);
        y=57; x=17;
        foreach(string label in new string[]{"`","1","2","3","4","5","6","7","8","9","0","−","="}) { AddKey(label,x,y,u); x+=u; }
        AddKey("⌫",x,y,u*2); Nav(y,"Ins","Home","PgUp");
        y=95; x=17; AddKey("Tab",x,y,u*1.5); x+=u*1.5;
        foreach(string label in new string[]{"Q","W","E","R","T","Y","U","I","O","P","[","]"}) { AddKey(label,x,y,u); x+=u; }
        AddKey("\\",x,y,u*1.5); Nav(y,"Del","End","PgDn");
        y=133; x=17; AddKey("Caps",x,y,u*1.75); x+=u*1.75;
        foreach(string label in new string[]{"A","S","D","F","G","H","J","K","L",";","'"}) { AddKey(label,x,y,u); x+=u; }
        AddKey("Enter",x,y,u*2.25);
        y=171; x=17; AddKey("Shift",x,y,u*2.25); x+=u*2.25;
        foreach(string label in new string[]{"Z","X","C","V","B","N","M",",",".","/"}) { AddKey(label,x,y,u); x+=u; }
        AddKey("Shift",x,y,u*2.75); AddKey("↑",662,y,u);
        y=209; x=17;
        foreach(string label in new string[]{"Ctrl","Win","Alt"}) { AddKey(label,x,y,u*1.25); x+=u*1.25; }
        AddKey("",x,y,u*6.25); x+=u*6.25;
        foreach(string label in new string[]{"Alt","Fn","Menu","Ctrl"}) { AddKey(label,x,y,u*1.25); x+=u*1.25; }
        Nav(y,"←","↓","→");
    }
    void Nav(double y,string a,string b,string c) { AddKey(a,624,y,38); AddKey(b,662,y,38); AddKey(c,700,y,38); }
    void AddKey(string label,double x,double y,double width)
    {
        Border key=new Border { Width=width-4,Height=34,Background=Brush("#111823"),BorderBrush=Brush("#B399FF"),BorderThickness=new Thickness(.8),CornerRadius=new CornerRadius(5) };
        Border face=new Border { Margin=new Thickness(2,1,2,4),Background=new LinearGradientBrush(Color.FromRgb(44,53,69),Color.FromRgb(27,35,49),90),BorderBrush=Brush("#354054"),BorderThickness=new Thickness(.5),CornerRadius=new CornerRadius(3) };
        TextBlock text=new TextBlock { Text=label,FontFamily=new FontFamily("Segoe UI"),FontWeight=FontWeights.Medium,FontSize=width<40 && label.Length>2?12:14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=Brush("#DCCBFF") };
        RenderOptions.SetClearTypeHint(face,ClearTypeHint.Enabled); TextOptions.SetTextRenderingMode(text,TextRenderingMode.ClearType); TextOptions.SetTextFormattingMode(text,TextFormattingMode.Display);
        face.Child=text; key.Child=face; keys.Add(key); legends.Add(text); Canvas.SetLeft(key,x); Canvas.SetTop(key,y); Get<Canvas>("KeyboardPreview").Children.Add(key);
    }
    void PaintKeyboard(bool rainbow)
    {
        Color selected=(Color)ColorConverter.ConvertFromString("#"+Current.Hex);
        keyboardGlow.Color=rainbow?Colors.MediumPurple:selected;
        keyboardGlow.Opacity=Current.Power?.10+.17*Current.Brightness/15.0:0;
        Color[] spectrum={Colors.MediumPurple,Colors.DeepSkyBlue,Colors.Turquoise,Colors.LightGreen,Colors.Orange,Colors.HotPink};
        for(int i=0;i<keys.Count;i++)
        {
            Color c=rainbow?spectrum[Math.Min(5,(int)(Canvas.GetLeft(keys[i])/760*6))]:selected;
            double strength=Current.Power?(.18+.82*Current.Brightness/15.0):0;
            Color stroke=Color.FromRgb((byte)(38+(c.R-38)*strength*.65),(byte)(45+(c.G-45)*strength*.65),(byte)(60+(c.B-60)*strength*.65));
            keys[i].BorderBrush=new SolidColorBrush(stroke);
            Color readable=Color.FromRgb((byte)(c.R*.35+255*.65),(byte)(c.G*.35+255*.65),(byte)(c.B*.35+255*.65));
            legends[i].Foreground=Current.Power?new SolidColorBrush(readable):Brush("#BCC6D6");
            legends[i].Opacity=1;
        }
    }
    void RefreshProfiles()
    {
        StackPanel panel=Get<StackPanel>("Profiles"); panel.Children.Clear();
        foreach(SavedProfile profile in Settings.Profiles)
        {
            SavedProfile chosen=profile;
            Grid row=new Grid { Margin=new Thickness(0,0,0,6) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(26) });
            Button select=new Button { Content=new TextBlock { Text=profile.Name,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=135 },FontSize=16,Padding=new Thickness(8,10,8,10),ToolTip=profile.Name,Background=Brush("#151C29"),BorderThickness=new Thickness(0) };
            select.Click+=delegate { Current=chosen.State.Clone(); UpdateUi(); Changed(); };
            Button remove=new Button { Content="×",FontSize=16,Padding=new Thickness(0),ToolTip="Remove profile",Style=(Style)Window.FindResource("Quiet") }; Grid.SetColumn(remove,1);
            remove.Click+=delegate { Settings.Profiles.Remove(chosen); SaveSettings(); RefreshProfiles(); };
            row.Children.Add(select); row.Children.Add(remove); panel.Children.Add(row);
        }
    }
    bool SaveSettings()
    {
        try { Storage.Save(Settings); return true; }
        catch(Exception ex) { Status("Could not save settings.",ex.Message,true); return false; }
    }
    Window Dialog(string title,int width,int height,out StackPanel body)
    {
        Window dialog=new Window { Title=title,Width=width,Height=height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Brush("#101620"),Foreground=Brush("#EDF0F7"),FontFamily=new FontFamily("Segoe UI"),FontSize=16 };
        if(!previewOnly)dialog.Owner=Window;
        foreach(object key in Window.Resources.Keys) dialog.Resources[key]=Window.Resources[key];
        body=new StackPanel { Margin=new Thickness(26) }; dialog.Content=new Border { Background=Brush("#101620"),Child=body }; return dialog;
    }
    void SaveProfile()
    {
        if(invalidHex) { Status("Enter a valid color first.","For example, #FFFFFF.",true); return; }
        if(Settings.Profiles.Count>=40) { Status("You already have 40 profiles.","Remove a profile before adding another.",true); return; }
        StackPanel body; Window dialog=Dialog("Save profile",400,250,out body);
        body.Children.Add(new TextBlock { Text="New profile",FontSize=23,FontWeight=FontWeights.SemiBold });
        TextBox name=new TextBox { Text="My profile",MaxLength=28,Margin=new Thickness(0,18,0,16) }; body.Children.Add(name);
        Button save=new Button { Content="Save profile",Style=(Style)Window.FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right,IsDefault=true };
        save.Click+=delegate
        {
            if(String.IsNullOrWhiteSpace(name.Text))return;
            SavedProfile existing=Settings.Profiles.Find(delegate(SavedProfile p) { return String.Equals(p.Name,name.Text.Trim(),StringComparison.OrdinalIgnoreCase); });
            if(existing!=null) existing.State=Current.Clone(); else Settings.Profiles.Add(new SavedProfile { Name=name.Text.Trim(),State=Current.Clone() });
            if(SaveSettings()) { RefreshProfiles(); dialog.Close(); Status("Profile saved","",false); }
        };
        body.Children.Add(save); dialog.Loaded+=delegate { name.Focus(); name.SelectAll(); }; dialog.ShowDialog();
    }
    void ShowSettings()
    {
        StackPanel body; Window dialog=Dialog("Settings",550,410,out body);
        body.Children.Add(new TextBlock { Text="Settings",FontSize=25,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,25) });
        CheckBox reconnect=new CheckBox { Content="Restore lighting on USB reconnect",IsChecked=Settings.Reconnect,Margin=new Thickness(0,0,0,18),ToolTip="Keep the app running." };
        body.Children.Add(reconnect);
        bool startup=StartupEnabled();
        CheckBox login=new CheckBox { Content="Start with Windows",IsChecked=startup,Margin=new Thickness(0,0,0,22),ToolTip="Starts minimized and restores lighting." }; body.Children.Add(login);
        body.Children.Add(new Border { Height=1,Background=Brush("#30394A"),Margin=new Thickness(0,0,0,20) });
        body.Children.Add(new TextBlock { Text="Bluetooth and Mac / Windows: use side switches.",Foreground=Brush("#A5B0C5"),FontSize=16,Margin=new Thickness(0,0,0,18) });
        StackPanel actions=new StackPanel { Orientation=Orientation.Horizontal };
        Button bluetooth=new Button { Content="Bluetooth settings",FontSize=16,Margin=new Thickness(0,0,10,0) };
        bluetooth.Click+=delegate { Open("ms-settings:bluetooth"); };
        Button logs=new Button { Content="Diagnostics",FontSize=16 };
        logs.Click+=delegate { Directory.CreateDirectory(Storage.Folder); Open(Storage.Folder); };
        actions.Children.Add(bluetooth); actions.Children.Add(logs); body.Children.Add(actions);
        TextBlock result=new TextBlock { Foreground=Brush("#FA8C9F"),FontSize=16,Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap }; body.Children.Add(result);
        Button done=new Button { Content="Save",Style=(Style)Window.FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0) };
        done.Click+=delegate
        {
            try
            {
                SetStartup(login.IsChecked==true); Settings.Reconnect=reconnect.IsChecked==true;
                if(SaveSettings()) { dialog.Close(); Status("Saved","",false); }
            }
            catch(Exception ex) { result.Text=ex.Message; }
        };
        body.Children.Add(done);
        if(DialogCapturePath!=null) { CaptureContent((FrameworkElement)dialog.Content,dialog.Width,dialog.Height-32,DialogCapturePath); return; }
        dialog.ShowDialog();
    }
    void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute=true }); } catch(Exception ex) { Status("Could not open settings.",ex.Message,true); } }
    internal static bool StartupEnabled()
    {
        using(RegistryKey key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key!=null && key.GetValue("K8Studio")!=null;
    }
    internal static void SetStartup(bool enabled)
    {
        using(RegistryKey key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if(enabled) key.SetValue("K8Studio","\""+Assembly.GetExecutingAssembly().Location+"\" --login");
            else key.DeleteValue("K8Studio",false);
        }
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr RegisterDeviceNotification(IntPtr handle,IntPtr filter,uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterDeviceNotification(IntPtr handle);
    void RegisterNotifications()
    {
        if(previewOnly)return;
        hwnd=HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle); hwnd.AddHook(DeviceMessage);
        Guid hid; Native.HidD_GetHidGuid(out hid);
        IntPtr filter=Marshal.AllocHGlobal(32);
        try
        {
            for(int i=0;i<32;i++) Marshal.WriteByte(filter,i,0);
            Marshal.WriteInt32(filter,0,32); Marshal.WriteInt32(filter,4,5);
            Marshal.StructureToPtr(hid,IntPtr.Add(filter,12),false);
            notification=RegisterDeviceNotification(hwnd.Handle,filter,0);
        }
        finally { Marshal.FreeHGlobal(filter); }
    }
    IntPtr DeviceMessage(IntPtr handle,int message,IntPtr wparam,IntPtr lparam,ref bool handled)
    {
        if(message!=0x219 || lparam==IntPtr.Zero)return IntPtr.Zero;
        int change=wparam.ToInt32();
        if((change!=0x8000 && change!=0x8004) || Marshal.ReadInt32(lparam,4)!=5)return IntPtr.Zero;
        string name=Marshal.PtrToStringUni(IntPtr.Add(lparam,28));
        if(name!=null && name.IndexOf("vid_05ac&pid_024f",StringComparison.OrdinalIgnoreCase)>=0)
        {
            arrivalPending=true;
            if(change==0x8004)SetConnection(false);
            arrivalTimer.Stop(); arrivalTimer.Start();
        }
        return IntPtr.Zero;
    }
    internal void RestoreOnStart(bool login=false) { restoreOnLogin=login; arrivalPending=login || Settings.Reconnect; }
    internal void Capture(string path)
    {
        CaptureContent((FrameworkElement)Window.Content,Window.Width,Window.Height,path);
    }
    static void CaptureContent(FrameworkElement content,double width,double height,string path)
    {
        content.Measure(new Size(width,height)); content.Arrange(new Rect(0,0,width,height)); content.UpdateLayout();
        RenderTargetBitmap bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
        PngBitmapEncoder encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(FileStream file=File.Create(path))encoder.Save(file);
    }
    internal void SetPreviewState(LightState state,double width,double height)
    {
        Current=state; Window.Width=width; Window.Height=height; UpdateUi();
    }
}

internal static class StudioProgram
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
    [STAThread] internal static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges",false);
        AppContext.SetSwitch("Switch.System.Windows.DoNotUsePresentationDpiCapabilityTier2OrGreater",false);
        try
        {
            if(args.Length>0 && args[0]=="--render")
            {
                Application app=new Application(); StudioWindow studio=new StudioWindow(true);
                if(args.Length>2) studio.SetPreviewState(new LightState { Hex=args[2],Effect=7 },args.Length>3?Double.Parse(args[3]):1160,args.Length>4?Double.Parse(args[4]):840);
                studio.Capture(args[1]); return 0;
            }
            if(args.Length>0 && args[0]=="--apply-white") { StudioLighting.Apply(new LightState()); return 0; }
            if(args.Length>0 && args[0]=="--self-test") { StudioTests.Run(args.Length>1?args[1]:null); return 0; }
            if(args.Length>0 && args[0]=="--ui-test") { StudioTests.Ui(args[1]); return 0; }
            if(args.Length>0 && args[0]=="--render-settings")
            {
                Application app=new Application(); StudioWindow studio=new StudioWindow(true); studio.DialogCapturePath=args[1];
                ((Button)studio.Window.FindName("Settings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return 0;
            }
            bool created;
            using(Mutex single=new Mutex(true,@"Local\K8Studio.Application",out created))
            {
                if(!created)
                {
                    IntPtr existing=FindWindow(null,"K8 Studio");
                    if(existing!=IntPtr.Zero) { ShowWindow(existing,9); SetForegroundWindow(existing); }
                    return 0;
                }
                Application app=new Application(); StudioWindow studio=new StudioWindow(false);
                bool login=args.Length>0 && args[0]=="--login"; studio.RestoreOnStart(login);
                if(login)studio.Window.WindowState=WindowState.Minimized;
                app.Run(studio.Window);
            }
            return 0;
        }
        catch(Exception ex)
        {
            try { Directory.CreateDirectory(Storage.Folder); File.WriteAllText(System.IO.Path.Combine(Storage.Folder,"error.log"),ex.ToString()); } catch { }
            if(args.Length==0) MessageBox.Show(ex.Message,"K8 Studio",MessageBoxButton.OK,MessageBoxImage.Error);
            return 1;
        }
    }
}



