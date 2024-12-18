﻿using System;
using System.Collections.Generic;
using System.Globalization;
#if NET5_0_OR_GREATER
using System.Text.Json.Serialization;
#endif
using System.Xml.Serialization;

namespace FlyleafLib;

public class Language : IEquatable<Language>
{
    public string       CultureName    { get => _CultureName; set
        {   // Required for XML load
            Culture = CultureInfo.GetCultureInfo(value);
            Refresh(this);
        }
    }
    string _CultureName;

    #if NET5_0_OR_GREATER
    [JsonIgnore]
    #endif
    [XmlIgnore]
    public string       TopEnglishName    { get; private set; }

    #if NET5_0_OR_GREATER
    [JsonIgnore]
    #endif
    [XmlIgnore]
    public CultureInfo  Culture         { get; private set; }

    #if NET5_0_OR_GREATER
    [JsonIgnore]
    #endif
    [XmlIgnore]
    public CultureInfo  TopCulture      { get; private set; }

    #if NET5_0_OR_GREATER
    [JsonIgnore]
    #endif
    [XmlIgnore]
    public string       IdSubLanguage   { get; private set; } // Can search for online subtitles with this id

    #if NET5_0_OR_GREATER
    [JsonIgnore]
    #endif
    [XmlIgnore]
    public string       OriginalInput   { get; private set; } // Only for Undetermined language (return clone)


    public override string ToString() => OriginalInput ?? TopEnglishName;

    public override int GetHashCode() => ToString().GetHashCode();

    public override bool Equals(object obj) => Equals(obj as Language);

    public bool Equals(Language lang)
    {
        if (lang is null)
            return false;

        if (lang.Culture == null && Culture == null)
        {
            if (OriginalInput != null || lang.OriginalInput != null)
                return OriginalInput == lang.OriginalInput;

            return true; // und
        }

        return lang.IdSubLanguage == IdSubLanguage; // TBR: top level will be equal with lower
    }

    public static bool operator ==(Language lang1, Language lang2) => lang1 is null ? lang2 is null ? true : false : lang1.Equals(lang2);

    public static bool operator !=(Language lang1, Language lang2) => !(lang1 == lang2);

    public static void Refresh(Language lang)
    {
        lang._CultureName = lang.Culture.Name;

        lang.TopCulture = lang.Culture;
        while (lang.TopCulture.Parent.Name != "")
            lang.TopCulture = lang.TopCulture.Parent;

        lang.TopEnglishName = lang.TopCulture.EnglishName;
        lang.IdSubLanguage = lang.Culture.ThreeLetterISOLanguageName;
    }

    public static Language Get(CultureInfo cult)
    {
        Language lang = new() { Culture = cult };
        Refresh(lang);

        return lang;
    }
    public static Language Get(string name)
    {
        Language lang = new() { Culture = StringToCulture(name) };
        if (lang.Culture != null)
            Refresh(lang);
        else
        {
            lang.IdSubLanguage  = "und";
            lang.TopEnglishName   = "Unknown";
            if (name != "und")
                lang.OriginalInput  = name;
        }

        return lang;
    }

    public static CultureInfo StringToCulture(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || lang.Length < 2)
            return null;

        string langLower = lang.ToLower();
        CultureInfo ret = null;

        try
        {
            ret = lang.Length == 3 ? ThreeLetterToCulture(langLower) : CultureInfo.GetCultureInfo(langLower);
        } catch { }

        // TBR: Check also -Country/region two letters?
        if (ret == null || ret.ThreeLetterISOLanguageName == "")
            foreach (var cult in CultureInfo.GetCultures(CultureTypes.AllCultures))
                if (cult.Name.ToLower() == langLower || cult.NativeName.ToLower() == langLower || cult.EnglishName.ToLower() == langLower)
                    return cult;

        return ret;
    }

    public static CultureInfo ThreeLetterToCulture(string lang)
    {
        if (lang == "zht")
            return CultureInfo.GetCultureInfo("zh-Hant");
        else if (lang == "pob")
            return CultureInfo.GetCultureInfo("pt-BR");
        else if (lang == "nor")
            return CultureInfo.GetCultureInfo("nob");
        else if (lang == "scc")
            return CultureInfo.GetCultureInfo("srp");
        else if (lang == "tgl")
            return CultureInfo.GetCultureInfo("fil");

        CultureInfo ret = CultureInfo.GetCultureInfo(lang);

        if (ret.ThreeLetterISOLanguageName == "")
        {
            ISO639_2B_TO_2T.TryGetValue(lang, out string iso639_2t);
            if (iso639_2t != null)
                ret = CultureInfo.GetCultureInfo(iso639_2t);
        }

        return ret.ThreeLetterISOLanguageName == "" ? null : ret;
    }

    public static readonly Dictionary<string, string> ISO639_2T_TO_2B = new()
    {
        { "bod","tib" },
        { "ces","cze" },
        { "cym","wel" },
        { "deu","ger" },
        { "ell","gre" },
        { "eus","baq" },
        { "fas","per" },
        { "fra","fre" },
        { "hye","arm" },
        { "isl","ice" },
        { "kat","geo" },
        { "mkd","mac" },
        { "mri","mao" },
        { "msa","may" },
        { "mya","bur" },
        { "nld","dut" },
        { "ron","rum" },
        { "slk","slo" },
        { "sqi","alb" },
        { "zho","chi" },
    };
    public static readonly Dictionary<string, string> ISO639_2B_TO_2T = new()
    {
        { "alb","sqi" },
        { "arm","hye" },
        { "baq","eus" },
        { "bur","mya" },
        { "chi","zho" },
        { "cze","ces" },
        { "dut","nld" },
        { "fre","fra" },
        { "geo","kat" },
        { "ger","deu" },
        { "gre","ell" },
        { "ice","isl" },
        { "mac","mkd" },
        { "mao","mri" },
        { "may","msa" },
        { "per","fas" },
        { "rum","ron" },
        { "slo","slk" },
        { "tib","bod" },
        { "wel","cym" },
    };

    public static Language English = Get("eng");
    public static Language Unknown = Get("und");
}