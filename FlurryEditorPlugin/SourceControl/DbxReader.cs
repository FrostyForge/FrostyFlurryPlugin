// Originally written by wannkunstbeikor
// Ported to v1.0.6.3 API with roundtrip XML deserialization support.

using Flurry.Editor.SourceControl;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace Flurry.Editor
{
    public sealed class DbxReader
    {
        private static readonly BindingFlags s_propertyBindingFlags = BindingFlags.Public | BindingFlags.Instance;

        private static readonly AccessTools.FieldRef<EbxAsset, List<int>> ebxAsset_refCountsAccessor = AccessTools.FieldRefAccess<EbxAsset, List<int>>("refCounts");
        private static readonly AccessTools.FieldRef<EbxAsset, List<object>> ebxAsset_objectsAccessor = AccessTools.FieldRefAccess<EbxAsset, List<object>>("objects");

        private readonly XmlDocument m_xml = new XmlDocument();
        private readonly Dictionary<Guid, (object ebxInstance, XmlNode xmlNode)> m_guidToObjAndXml = new Dictionary<Guid, (object, XmlNode)>();
        private readonly Dictionary<Guid, int> m_guidToRefCount = new Dictionary<Guid, int>();

        private EbxAsset m_ebx;
        private Guid m_primaryInstGuid;
        private int m_internalId = -1;

        public DbxReader(string filepath)
        {
            m_xml.Load(filepath);
        }

        public DbxReader(Stream inStream)
        {
            m_xml.Load(inStream);
        }

        public EbxAsset ReadAsset()
        {
            m_ebx = new EbxAsset();

            // EbxAsset's default constructor doesn't initialize its internal lists,
            // so we must do it via reflection before using the asset.
            InitializeEbxAssetLists(m_ebx);

            m_guidToObjAndXml.Clear();
            m_guidToRefCount.Clear();

            XmlNode rootNode = m_xml.DocumentElement;
            if (rootNode == null || rootNode.Name != "partition")
                throw new InvalidDataException("Invalid DBX: root element is not a partition");

            ReadPartition(rootNode);
            return m_ebx;
        }

        #region Partition Reading

        private void ReadPartition(XmlNode partitionNode)
        {
            Guid partitionGuid = Guid.Parse(GetAttr(partitionNode, "guid"));
            m_primaryInstGuid = Guid.Parse(GetAttr(partitionNode, "primaryInstance"));

            m_ebx.SetFileGuid(partitionGuid);

            foreach (XmlNode child in partitionNode.ChildNodes)
            {
                if (child.Name == "instance")
                    CreateInstance(child);
            }

            //App.Logger.Log($"Processing {m_guidToObjAndXml.Count} instances...");

            //foreach (var kvp in m_guidToObjAndXml)
            //{
            //  ParseInstance(kvp.Value.xmlNode, kvp.Value.ebxInstance, kvp.Key);
            //}

            FieldInfo refCountsField = typeof(EbxAsset).GetField("refCounts", BindingFlags.NonPublic | BindingFlags.Instance);
            if (refCountsField != null)
            {
                // We use a list to ensure the ref counts are in the exact same order as the objects
                List<int> refCounts = new List<int>();
                foreach (var kvp in m_guidToObjAndXml)
                {
                    ParseInstance(kvp.Value.xmlNode, kvp.Value.ebxInstance, kvp.Key);
                    refCounts.Add(m_guidToRefCount[kvp.Key]);
                }
                refCountsField.SetValue(m_ebx, refCounts);
            }
        }

        #endregion

        #region Instance Creation/Parsing

        private void CreateInstance(XmlNode node)
        {
            string typeName = GetAttr(node, "type");
            if (typeName.Contains("."))
                typeName = typeName.Split('.').Last();

            Type ebxType = TypeLibrary.GetType(typeName);
            if (ebxType == null)
                return;

            object obj = Activator.CreateInstance(ebxType);
            if (obj == null)
                return;

            bool isExported = bool.Parse(GetAttr(node, "exported"));
            Guid instGuid = Guid.Parse(GetAttr(node, "guid"));

            AssetClassGuid assetGuid = isExported
                ? new AssetClassGuid(instGuid, ++m_internalId)
                : new AssetClassGuid(++m_internalId);

            SetInstanceGuid(obj, assetGuid);
            m_guidToObjAndXml.Add(instGuid, (obj, node));
            m_guidToRefCount.Add(instGuid, 0);
        }

        private void ParseInstance(XmlNode node, object obj, Guid instGuid)
        {
            ReadInstanceFields(node, obj, obj.GetType());

            bool isRoot = Boolean.Parse(GetAttr(node, "root"));
            if (isRoot)
            {
                //App.Logger.Log($"Processing root object {instGuid.ToString()}");
                m_guidToRefCount[instGuid] = 0;
            }

            // We always use AddObject instead of AddRootObject to preserve the GUID
            // that was set in CreateInstance. AddObject also adds it to the objects list.
            m_ebx.AddObject(obj);
        }

        #endregion

        #region Field Reading

        public void ReadInstanceFields(XmlNode node, object obj, Type objType)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                ReadField(ref obj, child, objType);
            }
        }

        public void ReadField(ref object obj, XmlNode node, Type objType,
            bool isArray = false, bool isRef = false, Type arrayElementType = null, EbxFieldType? arrayElementFieldType = null)
        {
            switch (node.Name)
            {
                case "field":
                {
                    string refGuid = GetAttr(node, "ref");
                    string fieldName = GetAttr(node, "name");
                    if (refGuid != null)
                    {
                        SetProperty(obj, objType, fieldName, ParseRef(node, refGuid));
                    }
                    else
                    {
                        SetPropertyFromString(obj, objType, fieldName, node.InnerText);
                    }
                    break;
                }
                case "item":
                {
                    if (isRef)
                    {
                        string refGuid = GetAttr(node, "ref");
                        ((IList)obj)?.Add(ParseRef(node, refGuid));
                    }
                    else if (arrayElementType != null)
                    {
                        object value = GetValueFromString(arrayElementType, node.InnerText, arrayElementFieldType);
                        ((IList)obj)?.Add(value);
                    }
                    break;
                }
                case "array":
                {
                    string arrayFieldName = GetAttr(node, "name");
                    object array = ReadArray(node);
                    SetProperty(obj, objType, arrayFieldName, array);
                    break;
                }
                case "complex":
                {
                    string structFieldName = GetAttr(node, "name");
                    object structObj = ReadStruct(arrayElementType, node);
                    if (isArray)
                    {
                        ((IList)obj)?.Add(structObj);
                    }
                    else
                    {
                        SetProperty(obj, objType, structFieldName, structObj);
                    }
                    break;
                }
                case "boxed":
                {
                    ReadBoxedValueRef(obj, objType, node);
                    break;
                }
                case "typeref":
                {
                    ReadTypeRef(obj, objType, node);
                    break;
                }
                case "delegate":
                {
                    break;
                }
            }
        }

        #endregion

        #region Array Reading

        public object ReadArray(XmlNode node)
        {
            string arrayTypeStr = GetAttr(node, "type");
            bool isRef = arrayTypeStr.StartsWith("ref(");

            Type elementType;
            if (isRef)
            {
                elementType = typeof(PointerRef);
            }
            else
            {
                // Check primitives first — TypeLibrary.GetType("Int32") returns
                // FrostySdk.Ebx.Reflection.Int32 instead of System.Int32, which
                // causes type mismatch when assigning to List<int> properties.
                elementType = GetPrimitiveType(arrayTypeStr);
                if (elementType == null)
                {
                    elementType = TypeLibrary.GetType(arrayTypeStr);
                }
            }

            if (elementType == null)
                throw new InvalidDataException("Unknown array element type: " + arrayTypeStr);

            EbxFieldMetaAttribute elemMeta = elementType.GetCustomAttribute<EbxFieldMetaAttribute>();
            EbxFieldType? elemFieldType = elemMeta?.Type;

            Type listType = typeof(List<>).MakeGenericType(elementType);
            object array = Activator.CreateInstance(listType);

            foreach (XmlNode child in node.ChildNodes)
            {
                ReadField(ref array, child, listType, true, isRef, elementType, elemFieldType);
            }

            return array;
        }

        #endregion

        #region Struct Reading

        public object ReadStruct(Type structType, XmlNode node)
        {
            Type type = structType;
            if (type == null)
            {
                string typeName = GetAttr(node, "type");
                if (typeName != null)
                    type = TypeLibrary.GetType(typeName);
            }

            if (type == null)
                throw new InvalidDataException("Unknown struct type in XML");

            foreach (DbxConversionTransformer transformer in DbxConversionTransformer.Extensions) {
                if (transformer.IsTypeSupported(type)) {
                    return transformer.ReadFromDbx(this, node);
                }
            }

            object obj = Activator.CreateInstance(type);

            foreach (XmlNode child in node.ChildNodes)
            {
                ReadField(ref obj, child, type);
            }

            return obj;
        }

        #endregion

        #region BoxedValueRef Reading

        public void ReadBoxedValueRef(object parentObj, Type parentType, XmlNode node)
        {
            string fieldName = GetAttr(node, "name");
            string typeName = GetAttr(node, "typeName");

            BoxedValueRef boxed;
            if (typeName != null)
            {
                Type valueType = TypeLibrary.GetType(typeName) ?? GetPrimitiveType(typeName);
                EbxFieldMetaAttribute valueMeta = valueType?.GetCustomAttribute<EbxFieldMetaAttribute>();

                object value = null;
                string refGuid = GetAttr(node, "ref");
                if (refGuid != null)
                {
                    value = ParseRef(node, refGuid);
                }
                else if (node.FirstChild != null)
                {
                    switch (node.FirstChild.Name)
                    {
                        case "complex":
                            value = ReadStruct(null, node.FirstChild);
                            break;
                        case "array":
                            value = ReadArray(node.FirstChild);
                            break;
                        default:
                            if (valueType != null)
                                value = GetValueFromString(valueType, node.InnerText, valueMeta?.Type);
                            break;
                    }
                }

                EbxFieldType boxType = valueMeta?.Type ?? InferFieldType(value);
                boxed = new BoxedValueRef(value, boxType);
            }
            else
            {
                boxed = new BoxedValueRef();
            }

            SetProperty(parentObj, parentType, fieldName, boxed);
        }

        #endregion

        #region TypeRef Reading

        public void ReadTypeRef(object parentObj, Type parentType, XmlNode node)
        {
            string fieldName = GetAttr(node, "name");
            string typeName = GetAttr(node, "typeName");

            TypeRef typeRef = typeName != null ? new TypeRef(typeName) : new TypeRef();
            SetProperty(parentObj, parentType, fieldName, typeRef);
        }

        #endregion

        #region Pointer Resolution

        public PointerRef ParseRef(XmlNode node, string refGuid)
        {
            if (refGuid == "null")
                return new PointerRef();

            string partitionGuid = GetAttr(node, "partitionGuid");

            if (partitionGuid != null && partitionGuid != "null")
            {
                string[] parts = refGuid.Split('\\');
                Guid instanceGuid = Guid.Parse(parts[parts.Length - 1]);
                Guid fileGuid = Guid.Parse(partitionGuid);

                EbxImportReference import = new EbxImportReference
                {
                    FileGuid = fileGuid,
                    ClassGuid = instanceGuid
                };

                m_ebx.AddDependency(fileGuid);
                return new PointerRef(import);
            }
            else
            {
                Guid instGuid = Guid.Parse(refGuid);
                if (m_guidToObjAndXml.TryGetValue(instGuid, out var entry))
                {
                    m_guidToRefCount[instGuid]++;
                    return new PointerRef(entry.ebxInstance);
                }

                return new PointerRef();
            }
        }

        #endregion

        #region Property Setting

        public static void SetProperty(object obj, Type objType, string propName, object propValue)
        {
            PropertyInfo property = objType.GetProperty(propName, s_propertyBindingFlags);
            if (property != null && property.CanWrite && propValue != null)
            {
                property.SetValue(obj, propValue);
            }
        }

        public static void SetPropertyFromString(object obj, Type objType, string propName, string propValue)
        {
            PropertyInfo property = objType.GetProperty(propName, s_propertyBindingFlags);
            if (property == null || !property.CanWrite) return;

            EbxFieldMetaAttribute meta = property.GetCustomAttribute<EbxFieldMetaAttribute>();
            object value = GetValueFromString(property.PropertyType, propValue, meta?.Type);
            if (value != null)
                property.SetValue(obj, value);
        }

        public static object GetValueFromString(Type propType, string propValue, EbxFieldType? fieldType = null)
        {
            if (propType == typeof(CString)) return (CString)propValue;
            if (propType == typeof(string)) return propValue;
            if (propType == typeof(FileRef)) return (FileRef)propValue;
            if (propType == typeof(ResourceRef)) return (ResourceRef)ulong.Parse(propValue, System.Globalization.NumberStyles.HexNumber);
            if (propType == typeof(Sha1)) return new Sha1(propValue);
            if (propType == typeof(TypeRef)) return new TypeRef(propValue);
            if (propType == typeof(Guid)) return Guid.Parse(propValue);

            if (propType == typeof(bool)) return bool.Parse(propValue);
            if (propType == typeof(sbyte)) return sbyte.Parse(propValue);
            if (propType == typeof(short)) return short.Parse(propValue);
            if (propType == typeof(int)) return int.Parse(propValue);
            if (propType == typeof(long)) return long.Parse(propValue);
            if (propType == typeof(byte)) return byte.Parse(propValue);
            if (propType == typeof(ushort)) return ushort.Parse(propValue);
            if (propType == typeof(uint)) return uint.Parse(propValue);
            if (propType == typeof(ulong)) return ulong.Parse(propValue);
            if (propType == typeof(float)) return float.Parse(propValue);
            if (propType == typeof(double)) return double.Parse(propValue);

            if (propType.IsEnum) return Enum.Parse(propType, propValue);

            if (propType == typeof(PointerRef)) return new PointerRef();

            return null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// EbxAsset's default constructor leaves objects/dependencies/refCounts as null.
        /// Initialize them so AddObject/AddDependency don't crash.
        /// </summary>
        private static void InitializeEbxAssetLists(EbxAsset asset)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;

            var objectsField = typeof(EbxAsset).GetField("objects", flags);
            if (objectsField != null && objectsField.GetValue(asset) == null)
                objectsField.SetValue(asset, new List<object>());

            var depsField = typeof(EbxAsset).GetField("dependencies", flags);
            if (depsField != null && depsField.GetValue(asset) == null)
                depsField.SetValue(asset, new List<Guid>());

            var refCountsField = typeof(EbxAsset).GetField("refCounts", flags);
            if (refCountsField != null && refCountsField.GetValue(asset) == null)
                refCountsField.SetValue(asset, new List<int>());
        }

        public static string GetAttr(XmlNode node, string name)
        {
            return node.Attributes?.GetNamedItem(name)?.Value;
        }

        public static void SetInstanceGuid(object obj, AssetClassGuid guid)
        {
            FieldInfo fi = obj.GetType().GetField("__Guid", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null)
            {
                fi.SetValue(obj, guid);
            }
            else
            {
                ((dynamic)obj).SetInstanceGuid(guid);
            }
        }

        public static EbxFieldType InferFieldType(object value)
        {
            if (value == null) return EbxFieldType.Struct;
            Type t = value.GetType();
            if (t == typeof(bool)) return EbxFieldType.Boolean;
            if (t == typeof(int)) return EbxFieldType.Int32;
            if (t == typeof(float)) return EbxFieldType.Float32;
            if (t == typeof(string) || t == typeof(CString)) return EbxFieldType.CString;
            if (t == typeof(PointerRef)) return EbxFieldType.Pointer;
            if (t.IsEnum) return EbxFieldType.Enum;
            if (t.IsValueType) return EbxFieldType.Struct;
            return EbxFieldType.Struct;
        }

        public static Type GetPrimitiveType(string name)
        {
            switch (name)
            {
                case "String": return typeof(String);
                case "CString": return typeof(CString);
                case "Boolean": return typeof(Boolean);
                case "Int8": return typeof(SByte);
                case "Int16": return typeof(Int16);
                case "Int32": return typeof(Int32);
                case "Int64": return typeof(Int64);
                case "UInt8": return typeof(Byte);
                case "UInt16": return typeof(UInt16);
                case "UInt32": return typeof(UInt32);
                case "UInt64": return typeof(UInt64);
                case "Float32": return typeof(Single);
                case "Float64": return typeof(Double);
                case "Single": return typeof(Single);
                case "Guid": return typeof(Guid);
                case "Sha1": return typeof(Sha1);
                case "ResourceRef": return typeof(ResourceRef);
                case "FileRef": return typeof(FileRef);
                case "TypeRef": return typeof(TypeRef);
                case "BoxedValueRef": return typeof(BoxedValueRef);
                // Note that PointerRef is not listed this way in Frosty v2
                // If having issues with PointerRef serialization, check here.
                case "PointerRef": return typeof(PointerRef);
                default: return null;
            }
        }

        #endregion
    }
}
