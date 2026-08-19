using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AICon
{
    // Shared list of (model category, its tag category, display label) pairs AR400 knows how to offer
    // for tagging — the well-known architectural/MEP/structural categories, matching the categories
    // Revit's own "Tag All Not Tagged" command offers. Used by both PackageSettingsWindow (the "Tags"
    // button) and TagCategoriesWindow (the per-category tag-type/leader picker).
    internal static class TaggableCategories
    {
        internal static readonly (BuiltInCategory Model, BuiltInCategory Tag, string Label)[] Defs =
        {
            (BuiltInCategory.OST_Rooms, BuiltInCategory.OST_RoomTags, "Rooms"),
            (BuiltInCategory.OST_Walls, BuiltInCategory.OST_WallTags, "Walls"),
            (BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags, "Doors"),
            (BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags, "Windows"),
            (BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags, "Furniture"),
            (BuiltInCategory.OST_Casework, BuiltInCategory.OST_CaseworkTags, "Casework"),
            (BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CeilingTags, "Ceilings"),
            (BuiltInCategory.OST_Floors, BuiltInCategory.OST_FloorTags, "Floors"),
            (BuiltInCategory.OST_Columns, BuiltInCategory.OST_ColumnTags, "Architectural Columns"),
            (BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumnTags, "Structural Columns"),
            (BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags, "Structural Framing"),
            (BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags, "Generic Models"),
            (BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags, "Plumbing Fixtures"),
            (BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags, "Mechanical Equipment"),
            (BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalFixtureTags, "Electrical Fixtures"),
            (BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalEquipmentTags, "Electrical Equipment"),
            (BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_SpecialityEquipmentTags, "Specialty Equipment"),
            (BuiltInCategory.OST_Parking, BuiltInCategory.OST_ParkingTags, "Parking"),
            (BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsTags, "Stairs"),
        };

        /// <summary>Categories THIS project can actually tag right now: real elements present AND a
        /// loaded tag family for that category — "what can I tag", not a generic wishlist.</summary>
        internal static List<(BuiltInCategory Model, BuiltInCategory Tag, string Label)> Available(Document doc)
        {
            var result = new List<(BuiltInCategory, BuiltInCategory, string)>();
            foreach (var def in Defs)
            {
                bool hasElements = new FilteredElementCollector(doc).OfCategory(def.Model).WhereElementIsNotElementType().Any();
                if (!hasElements) continue;
                bool hasTagFamily = new FilteredElementCollector(doc).OfCategory(def.Tag).WhereElementIsElementType().Any();
                if (!hasTagFamily) continue;
                result.Add((def.Model, def.Tag, def.Label));
            }
            return result;
        }

        /// <summary>Loaded tag types (Family : Type) for a tag category, as (typeId, label) pairs.</summary>
        internal static List<(int TypeId, string Label)> LoadedTagTypes(Document doc, BuiltInCategory tagCategory)
        {
            return new FilteredElementCollector(doc).OfCategory(tagCategory).WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(s => s.Family.Name).ThenBy(s => s.Name)
                .Select(s => (s.Id.ToInt(), s.Family.Name + " : " + s.Name))
                .ToList();
        }
    }
}
