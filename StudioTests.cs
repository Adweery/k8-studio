// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Windows;
using System.Windows.Controls;
internal static class StudioTests
{
    static void Check(bool condition,string name) { if(!condition)throw new Exception("TEST FAILED: "+name); }
    internal static void Run(string reportPath)
    {
        LightState state=new LightState { Hex="12ABEF",Effect=7,Brightness=9,Speed=3 };
        var pages=StudioLighting.Pages(state);
        Check(pages.Count==18,"18-page protocol framing");
        foreach(byte[] page in pages)Check(page.Length==64,"64-byte payloads");
        Check(pages[17][0]==7 && pages[17][1]==0x12 && pages[17][2]==0xAB && pages[17][3]==0xEF,"selected effect and RGB channels");
        Check(pages[17][9]==9 && pages[17][10]==12 && pages[17][14]==0xAA && pages[17][15]==0x55,"brightness, speed and check bytes");
        state.Power=false; pages=StudioLighting.Pages(state);
        Check(pages[17][0]==0x80 && pages[17][9]==0,"off uses dedicated hardware mode");
        state.Power=true; state.Effect=10; state.Direction=3; pages=StudioLighting.Pages(state); Check(pages[17][11]==3,"vertical direction");
        state.Effect=11; state.Direction=1; pages=StudioLighting.Pages(state); Check(pages[17][11]==1,"horizontal direction");
        state.Rainbow=true; pages=StudioLighting.Pages(state); Check(pages[17][8]==1,"multicolor flag");
        for(int effect=1;effect<=19;effect++) { state.Effect=effect; pages=StudioLighting.Pages(state); Check(pages[17][0]==effect,"effect selection "+effect); }
        state.Brightness=0; bool rejected=false;
        try { StudioLighting.Pages(state); } catch(ArgumentException) { rejected=true; }
        Check(rejected,"invalid brightness rejected before USB access");
        state.Brightness=15; state.Hex="G00000"; rejected=false;
        try { StudioLighting.Pages(state); } catch(FormatException) { rejected=true; }
        Check(rejected,"invalid hex rejected before USB access");
        StudioSettings settings=new StudioSettings(); settings.Profiles.Add(new SavedProfile { Name="Nočná nálada",State=new LightState { Hex="BA98FE",Effect=7 } });
        using(MemoryStream memory=new MemoryStream())
        {
            XmlSerializer serializer=new XmlSerializer(typeof(StudioSettings)); serializer.Serialize(memory,settings); memory.Position=0;
            StudioSettings restored=(StudioSettings)serializer.Deserialize(memory);
            Check(restored.Profiles[0].Name=="Nočná nálada" && restored.Profiles[0].State.Hex=="BA98FE" && restored.Reconnect,"profile serialization including accents and reconnect default");
        }
        using(MemoryStream memory=new MemoryStream())
        {
            new XmlSerializer(typeof(StudioSettings)).Serialize(memory,settings); memory.Position=0;
            Check(Storage.Read(memory).Profiles[0].Name=="Nočná nálada","bounded settings reader accepts existing profile format");
        }
        string untrusted="<!DOCTYPE StudioSettings [<!ENTITY probe SYSTEM 'file:///K8Studio-security-test-not-present.txt'>]><StudioSettings><Last><Hex>&probe;</Hex></Last></StudioSettings>";
        rejected=false;
        using(MemoryStream memory=new MemoryStream(Encoding.UTF8.GetBytes(untrusted)))
        {
            try { Storage.Read(memory); }
            catch(InvalidOperationException ex) { rejected=ex.InnerException is XmlException; }
            catch(XmlException) { rejected=true; }
        }
        Check(rejected,"DTD and external entities rejected before resolution");
        rejected=false;
        using(MemoryStream memory=new MemoryStream(new byte[262145]))
        { try { Storage.Read(memory); } catch(InvalidDataException) { rejected=true; } }
        Check(rejected,"oversized settings rejected before parsing");
        rejected=false;
        using(MemoryStream memory=new MemoryStream(Encoding.UTF8.GetBytes("<StudioSettings>"+new string(' ',131073)+"</StudioSettings>")))
        {
            try { Storage.Read(memory); }
            catch(InvalidOperationException ex) { rejected=ex.InnerException is XmlException; }
            catch(XmlException) { rejected=true; }
        }
        Check(rejected,"XML document character quota enforced");
        settings.Profiles[0].Name=new string('A',29); rejected=false;
        using(MemoryStream memory=new MemoryStream())
        {
            new XmlSerializer(typeof(StudioSettings)).Serialize(memory,settings); memory.Position=0;
            try { Storage.Read(memory); } catch(InvalidDataException) { rejected=true; }
        }
        Check(rejected,"invalid oversized profile names rejected");
        if(reportPath!=null)File.WriteAllText(reportPath,"PASS: protocol framing, all 19 effect selections, RGB, brightness, inverse speed, directions, multicolor, power OFF, input validation, profile serialization, DTD/external-entity rejection, XML/file size limits and profile name limits.\r\n");
    }
    internal static void Ui(string reportPath)
    {
        Application app=new Application(); StudioWindow studio=new StudioWindow(true);
        TextBox hex=(TextBox)studio.Window.FindName("Hex"); Slider red=(Slider)studio.Window.FindName("Red");
        hex.Text="#204080"; Check(studio.Current.Hex=="204080" && red.Value==32,"hex updates RGB sliders");
        red.Value=128; Check(studio.Current.Hex=="804080" && hex.Text=="#804080","RGB slider updates hex and state");
        hex.Text="#INVALID"; Check(!((Button)studio.Window.FindName("Apply")).IsEnabled,"invalid hex blocks apply");
        hex.Text="#FFFFFF"; Check(((Button)studio.Window.FindName("Apply")).IsEnabled,"valid hex restores apply");
        ComboBox effect=(ComboBox)studio.Window.FindName("Effect"); effect.SelectedIndex=9;
        Check(studio.Current.Effect==10 && studio.Current.Direction==2 && ((ComboBox)studio.Window.FindName("Direction")).IsEnabled,"vertical effect enables direction");
        effect.SelectedIndex=0; Check(!((Slider)studio.Window.FindName("Speed")).IsEnabled,"static disables speed");
        effect.SelectedIndex=7; Check(!hex.IsEnabled && !((CheckBox)studio.Window.FindName("Rainbow")).IsEnabled,"spectrum disables fixed-color controls");
        effect.SelectedIndex=6;
        ((CheckBox)studio.Window.FindName("Rainbow")).IsChecked=true;
        Check(studio.Current.Rainbow && !((Grid)studio.Window.FindName("RgbControls")).IsEnabled,"multicolor disables fixed-color sliders");
        ((Button)studio.Window.FindName("Power")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!studio.Current.Power,"power button toggles state");
        ((Button)studio.Window.FindName("White")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(studio.Current.Power && studio.Current.Effect==1 && studio.Current.Hex=="FFFFFF","white button restores power and static white");
        Check(((Canvas)studio.Window.FindName("KeyboardPreview")).Children.Count==88,"87-key TKL preview");
        File.WriteAllText(reportPath,"PASS: hex/RGB synchronization, invalid-input blocking, effect-specific controls, multicolor, power and white actions, 87-key preview. No hardware writes or profile storage changes.\r\n");
    }
}
