// Temp file to explore CardModel lookup API
// We'll look at the assembly to find how to create/find cards by Entry string.

using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace SharedDraft;

public static class CardApiExplorer
{
    public static void Explore()
    {
        var asm = typeof(CardModel).Assembly;
        
        // 1. Check CardModel.Id property type
        var idProp = typeof(CardModel).GetProperty("Id");
        ModEntry.Logger.Info($"CardModel.Id type: {idProp?.PropertyType.FullName}");
        
        // 2. Check if ModelId has a static Create/From method
        if (idProp != null)
        {
            var idType = idProp.PropertyType;
            foreach (var m in idType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                ModEntry.Logger.Info($"ModelId static: {m.ReturnType.Name} {m.Name}({pars})");
            }
            foreach (var c in idType.GetConstructors())
            {
                var pars = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                ModEntry.Logger.Info($"ModelId ctor: ({pars})");
            }
        }
        
        // 3. Look for types with "CardFactory" in name
        var factoryTypes = asm.GetTypes()
            .Where(t => t.Name.Contains("CardFactory") || 
                        t.Name.Contains("CardRegistry") ||
                        t.Name.Contains("CardDatabase"))
            .Take(10);
        foreach (var ft in factoryTypes)
        {
            ModEntry.Logger.Info($"Found type: {ft.FullName}");
            foreach (var m in ft.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Take(20))
            {
                ModEntry.Logger.Info($"  {m.ReturnType.Name} {m.Name}");
            }
        }
        
        // 4. Look for "GameDef" or similar
        var gameDefTypes = asm.GetTypes()
            .Where(t => t.Name.Contains("GameDef") || t.Name.Contains("ModelCache") || t.Name.Contains("ModelRegistry"))
            .Take(10);
        foreach (var gdt in gameDefTypes)
        {
            ModEntry.Logger.Info($"GameDef type: {gdt.FullName}");
            foreach (var m in gdt.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Take(15))
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                ModEntry.Logger.Info($"  {m.ReturnType.Name} {m.Name}({pars})");
            }
        }

        // 5. Check ModelIdSerializationCache
        var cacheType = asm.GetTypes().FirstOrDefault(t => t.Name == "ModelIdSerializationCache");
        if (cacheType != null)
        {
            ModEntry.Logger.Info($"ModelIdSerializationCache: {cacheType.FullName}");
            foreach (var m in cacheType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Take(20))
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                ModEntry.Logger.Info($"  {m.ReturnType.Name} {m.Name}({pars})");
            }
        }
    }
}
