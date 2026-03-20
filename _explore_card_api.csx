// Explore CardModel API in sts2.dll
#r "/Users/guyinan/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64/sts2.dll"

using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

var cardModelType = typeof(CardModel);
Console.WriteLine("=== CardModel ===");
Console.WriteLine("Assembly: " + cardModelType.Assembly.FullName);
Console.WriteLine("BaseType: " + cardModelType.BaseType?.FullName);

// Check for static methods/properties on CardModel
Console.WriteLine("\n--- Static methods/props ---");
foreach (var m in cardModelType.GetMethods(BindingFlags.Public | BindingFlags.Static))
    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
foreach (var p in cardModelType.GetProperties(BindingFlags.Public | BindingFlags.Static))
    Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");

// Look at ModelId<T>
Console.WriteLine("\n=== ModelId ===");
var idProp = cardModelType.GetProperty("Id");
if (idProp != null) {
    var idType = idProp.PropertyType;
    Console.WriteLine("Type: " + idType.FullName);
    foreach (var m in idType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
    foreach (var p in idType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  Prop: {p.PropertyType.Name} {p.Name}");
}

// Look for CardFactory
Console.WriteLine("\n=== CardFactory search ===");
var types = cardModelType.Assembly.GetTypes().Where(t => t.Name.Contains("CardFactory") || t.Name.Contains("CardDatabase")).Take(10);
foreach (var t in types) {
    Console.WriteLine($"  {t.FullName}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Take(15))
        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}");
}
