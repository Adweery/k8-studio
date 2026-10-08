// SPDX-License-Identifier: GPL-2.0-or-later
// HID lighting framing adapted from OpenRGB, Morgan Guimard, 2022.
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

public class LightState
{
    public string Hex = "FFFFFF";
    public int Effect = 1;
    public int Brightness = 15;
    public int Speed = 8;
    public int Direction = 0;
    public bool Rainbow = false;
    public bool Power = true;
    public LightState Clone() { return (LightState)MemberwiseClone(); }
    public void Validate()
    {
        if(Hex==null || Hex.Length!=6) throw new ArgumentException("Color must contain 6 hexadecimal digits.");
        Convert.ToUInt32(Hex,16);
        if(Effect<1 || Effect>19 || Brightness<1 || Brightness>15 || Speed<0 || Speed>15 || Direction<0 || Direction>3)
            throw new ArgumentException("Invalid lighting settings.");
    }
}
public class SavedProfile { public string Name = "My profile"; public LightState State = new LightState(); }
public class StudioSettings
{
    public LightState Last = new LightState();
    public bool Reconnect = true;
    public List<SavedProfile> Profiles = new List<SavedProfile>();
}
internal static class Storage
{
    internal static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K8Studio");
    internal static readonly string SettingsPath=Path.Combine(Folder,"settings.xml");
    internal static readonly string LogPath=Path.Combine(Folder,"last-run.log");
    internal static StudioSettings Load()
    {
        if(!File.Exists(SettingsPath)) return new StudioSettings();
        using(FileStream stream=File.OpenRead(SettingsPath))
            return Read(stream);
    }
    internal static StudioSettings Read(Stream stream)
    {
        if(stream.CanSeek && stream.Length>262144)
            throw new InvalidDataException("The settings file is too large.");
        XmlReaderSettings options=new XmlReaderSettings
        {
            DtdProcessing=DtdProcessing.Prohibit,
            XmlResolver=null,
            MaxCharactersInDocument=131072,
            MaxCharactersFromEntities=1024
        };
        using(XmlReader reader=XmlReader.Create(stream,options))
        {
            StudioSettings settings=(StudioSettings)new XmlSerializer(typeof(StudioSettings)).Deserialize(reader);
            if(settings.Last==null || settings.Profiles==null) throw new InvalidDataException("Invalid profile file.");
            settings.Last.Validate();
            if(settings.Profiles.Count>40) throw new InvalidDataException("Too many profiles.");
            foreach(SavedProfile p in settings.Profiles)
            { if(p==null || p.State==null || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length>28) throw new InvalidDataException("Invalid profile."); p.State.Validate(); }
            return settings;
        }
    }
    internal static void Save(StudioSettings settings)
    {
        settings.Last.Validate(); Directory.CreateDirectory(Folder);
        string temp=SettingsPath+".tmp";
        using(FileStream stream=File.Create(temp)) new XmlSerializer(typeof(StudioSettings)).Serialize(stream,settings);
        if(File.Exists(SettingsPath)) File.Replace(temp,SettingsPath,SettingsPath+".bak");
        else File.Move(temp,SettingsPath);
    }
}
internal static class StudioLighting
{
    internal static List<byte[]> Pages(LightState state)
    {
        state.Validate();
        uint rgb=Convert.ToUInt32(state.Hex,16);
        byte red=(byte)(rgb>>16), green=(byte)(rgb>>8), blue=(byte)rgb;
        List<byte[]> pages=new List<byte[]>();
        for(int p=0;p<5;p++)
        {
            byte[] data=new byte[64];
            for(int j=0;j<4;j++)
            {
                int effect=p*4+j+1, offset=j*16;
                data[offset]=(byte)(effect==20?0x80:effect);
                if(effect!=6 && effect!=8 && effect!=20)
                { data[offset+1]=red; data[offset+2]=green; data[offset+3]=blue; }
                data[offset+8]=(byte)(state.Rainbow && effect!=20 ? 1:0);
                data[offset+9]=(byte)(effect==20?0:state.Brightness);
                data[offset+10]=(byte)(effect==20?0:15-state.Speed);
                data[offset+11]=(byte)(effect==10? (state.Direction>=2?state.Direction:2):effect==11?(state.Direction<=1?state.Direction:0):0);
                data[offset+14]=0xAA; data[offset+15]=0x55;
            }
            pages.Add(data);
        }
        for(int i=0;i<3;i++) pages.Add(new byte[64]);
        for(int p=0;p<9;p++)
        {
            byte[] data=new byte[64];
            for(int j=0;j<64;j+=4) { data[j]=0x80; data[j+1]=red; data[j+2]=green; data[j+3]=blue; }
            pages.Add(data);
        }
        int index=state.Power?state.Effect-1:19;
        byte[] active=new byte[64]; Array.Copy(pages[index/4],(index%4)*16,active,0,16); pages.Add(active);
        return pages;
    }
    internal static void Apply(LightState state)
    {
        List<byte[]> pages=Pages(state);
        Directory.CreateDirectory(Storage.Folder);
        using(StreamWriter log=new StreamWriter(Storage.LogPath,false))
        using(Keyboard keyboard=Keyboard.Find())
        {
            log.AutoFlush=true; log.WriteLine(DateTimeOffset.Now.ToString("o"));
            log.WriteLine("Keychron K8 / "+state.Hex+" / effect="+state.Effect+" / brightness="+state.Brightness+" / speed="+state.Speed+" / power="+state.Power);
            bool transaction=false;
            try
            {
                log.WriteLine(BitConverter.ToString(keyboard.Command(0x19,true,0)));
                transaction=true;
                log.WriteLine(BitConverter.ToString(keyboard.Command(0x13,true,18)));
                foreach(byte[] page in pages) keyboard.Send(page);
                log.WriteLine(BitConverter.ToString(keyboard.Command(0x02,true,0))); transaction=false;
                keyboard.Command(0xF0,false,0);
                log.WriteLine("22 HID reports accepted. Device appearance must be confirmed visually.");
            }
            catch(Exception ex)
            {
                log.WriteLine(ex.ToString());
                if(transaction) { try { keyboard.Command(0x02,false,0); } catch { } }
                throw;
            }
        }
    }
}

