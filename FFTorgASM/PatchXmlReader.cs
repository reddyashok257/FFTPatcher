﻿using System;
using System.Collections.Generic;
using System.Xml;
using PatcherLib.Datatypes;
using PatcherLib.Iso;
using ASMEncoding;
using System.IO;
using System.Text;
using ASMEncoding.Helpers;

namespace FFTorgASM
{
    public class SpecificLocation
    {
        public PsxIso.Sectors Sector { get; set; }
        public string OffsetString { get; set; }

        public SpecificLocation(PsxIso.Sectors sector, string offsetString)
        {
            Sector = sector;
            OffsetString = offsetString;
        }
    }

    static class PatchXmlReader
    {
        public static readonly System.Text.RegularExpressions.Regex stripRegex = 
            new System.Text.RegularExpressions.Regex( @"\s" );

        public static bool TryGetPatches( string xmlString, string xmlFilename, ASMEncodingUtility asmUtility, out IList<AsmPatch> patches )
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml( xmlString );
                patches = GetPatches( doc.SelectSingleNode( "/Patches" ), xmlFilename, asmUtility );
                return true;
            }
            catch ( Exception ex )
            {
                patches = null;
                return false;
            }
        }

        private static void GetPatchNameAndDescription( XmlNode node, out string name, out string description )
        {
            name = node.Attributes["name"].InnerText;
            XmlNode descriptionNode = node.SelectSingleNode( "Description" );
            description = name;
            if ( descriptionNode != null )
            {
                description = descriptionNode.InnerText;
            }

        }

        private static void GetPatch( XmlNode node, string xmlFileName, ASMEncodingUtility asmUtility, List<VariableType> variables,
            out string name, out string description, out IList<PatchedByteArray> staticPatches, out List<bool> outDataSectionList, out bool hideInDefault)
        {
            GetPatchNameAndDescription( node, out name, out description );

            hideInDefault = false;
            XmlAttribute attrHideInDefault = node.Attributes["hideInDefault"];
            if (attrHideInDefault != null)
            {
                if (attrHideInDefault.InnerText.ToLower().Trim() == "true")
                    hideInDefault = true;
            }

            bool hasDefaultSector = false;
            PsxIso.Sectors defaultSector = (PsxIso.Sectors)0;
            XmlAttribute attrDefaultFile = node.Attributes["file"];
            XmlAttribute attrDefaultSector = node.Attributes["sector"];

            if (attrDefaultFile != null)
            {
                defaultSector = (PsxIso.Sectors)Enum.Parse(typeof(PsxIso.Sectors), attrDefaultFile.InnerText);
                hasDefaultSector = true;
            }
            else if (attrDefaultSector != null)
            {
                defaultSector = (PsxIso.Sectors)Int32.Parse(attrDefaultSector.InnerText, System.Globalization.NumberStyles.HexNumber);
                hasDefaultSector = true;
            }

            XmlNodeList currentLocs = node.SelectNodes( "Location" );
            List<PatchedByteArray> patches = new List<PatchedByteArray>( currentLocs.Count );
            List<bool> isDataSectionList = new List<bool>( currentLocs.Count );

            foreach ( XmlNode location in currentLocs )
            {
                //UInt32 offset = UInt32.Parse( location.Attributes["offset"].InnerText, System.Globalization.NumberStyles.HexNumber );
                XmlAttribute offsetAttribute = location.Attributes["offset"];
                XmlAttribute fileAttribute = location.Attributes["file"];
                XmlAttribute sectorAttribute = location.Attributes["sector"];
                XmlAttribute modeAttribute = location.Attributes["mode"];
                XmlAttribute offsetModeAttribute = location.Attributes["offsetMode"];
                XmlAttribute inputFileAttribute = location.Attributes["inputFile"];
                XmlAttribute replaceLabelsAttribute = location.Attributes["replaceLabels"];
                XmlAttribute attrLabel = location.Attributes["label"];
                XmlAttribute attrSpecific = location.Attributes["specific"];

                string strOffsetAttr = (offsetAttribute != null) ? offsetAttribute.InnerText : "";
                string[] strOffsets = strOffsetAttr.Replace(" ", "").Split(',');
                bool ignoreOffsetMode = false;
                bool isSpecific = false;

                List<SpecificLocation> specifics = FillSpecificAttributeData(attrSpecific, defaultSector);

                if (specifics.Count > 0)
                {
                    isSpecific = true;
                    List<string> newStrOffsets = new List<string>(specifics.Count);
                    foreach (SpecificLocation specific in specifics)
                        newStrOffsets.Add(specific.OffsetString);
                    strOffsets = newStrOffsets.ToArray();
                }
                else if ((string.IsNullOrEmpty(strOffsetAttr)) && (patches.Count > 0))
                {
                    // No offset defined -- offset is (last patch offset) + (last patch size)
                    PatchedByteArray lastPatchedByteArray = patches[patches.Count - 1];
                    long offset = lastPatchedByteArray.Offset + lastPatchedByteArray.GetBytes().Length;
                    string strOffset = offset.ToString("X");
                    strOffsets = new string[1] { strOffset };
                    ignoreOffsetMode = true;
                }

                PsxIso.Sectors sector = (PsxIso.Sectors)0;
                if (isSpecific)
                {
                    sector = specifics[0].Sector;
                }
                else if (fileAttribute != null)
                {
                    sector = (PsxIso.Sectors)Enum.Parse( typeof( PsxIso.Sectors ), fileAttribute.InnerText );
                }
                else if (sectorAttribute != null)
                {
                    sector = (PsxIso.Sectors)Int32.Parse( sectorAttribute.InnerText, System.Globalization.NumberStyles.HexNumber );
                }
                else if (hasDefaultSector)
                {
                    sector = defaultSector;
                }
                else if (patches.Count > 0)
                {
                    sector = (PsxIso.Sectors)(patches[patches.Count - 1].Sector);
                }
                else
                {
                    throw new Exception("Error in patch XML: Invalid file/sector!");
                }

                bool asmMode = false;
                bool markedAsData = false;
                if (modeAttribute != null)
                {
                    string modeAttributeText = modeAttribute.InnerText.ToLower().Trim();
                    if (modeAttributeText == "asm")
                	{
                		asmMode = true;
                	}
                    else if (modeAttributeText == "data")
                    {
                        markedAsData = true;
                    }
                }
                
                bool isRamOffset = false;
                if ((!ignoreOffsetMode) && (offsetModeAttribute != null))
                {
                	if (offsetModeAttribute.InnerText.ToLower().Trim() == "ram")
                		isRamOffset = true;
                }

                string content = location.InnerText;
                if (inputFileAttribute != null)
                {
                    using (StreamReader streamReader = new StreamReader(inputFileAttribute.InnerText, Encoding.UTF8))
                    {
                        content = streamReader.ReadToEnd();
                    }
                }

                bool replaceLabels = false;
                if (replaceLabelsAttribute != null)
                {
                    if (replaceLabelsAttribute.InnerText.ToLower().Trim() == "true")
                        replaceLabels = true;
                }
                if (replaceLabels)
                {
                    content = asmUtility.ReplaceLabelsInHex(content, true);
                }

                string label = "";
                if (attrLabel != null)
                {
                    label = attrLabel.InnerText.Replace(" ", "");
                }

                Nullable<PsxIso.FileToRamOffsets> ftrOffset = null;
                try
                {
                    ftrOffset = (PsxIso.FileToRamOffsets)Enum.Parse(typeof(PsxIso.FileToRamOffsets), "OFFSET_" + Enum.GetName(typeof(PsxIso.Sectors), sector));
                }
                catch (Exception)
                {
                    ftrOffset = null;
                }

                int offsetIndex = 0;
                foreach (string strOffset in strOffsets)
                {
                    UInt32 offset = UInt32.Parse(strOffset, System.Globalization.NumberStyles.HexNumber);

                    UInt32 ramOffset = offset;
                    UInt32 fileOffset = offset;

                    if (ftrOffset.HasValue)
                    {
                        try
                        {
                            if (isRamOffset)
                                fileOffset -= (UInt32)ftrOffset;
                            else
                                ramOffset += (UInt32)ftrOffset;
                        }
                        catch (Exception) { }
                    }

                    ramOffset = ramOffset | 0x80000000;     // KSEG0

                    byte[] bytes;
                    string errorText = "";
                    if (asmMode)
                    {
                        string encodeContent = content;

                        string strPrefix = "";
                        foreach (PatchedByteArray currentPatchedByteArray in patches)
                        {
                            if (!string.IsNullOrEmpty(currentPatchedByteArray.Label))
                                strPrefix += String.Format(".label @{0}, {1}\r\n", currentPatchedByteArray.Label, currentPatchedByteArray.RamOffset);
                        }
                        foreach (VariableType variable in variables)
                        {
                            strPrefix += String.Format(".eqv %{0}, {1}\r\n", ASMStringHelper.RemoveSpaces(variable.name).Replace(",", ""), AsmPatch.GetUnsignedByteArrayValue_LittleEndian(variable.byteArray));
                        }

                        encodeContent = strPrefix + content;

                        ASMEncoderResult result = asmUtility.EncodeASM(encodeContent, ramOffset);
                        bytes = result.EncodedBytes;
                        errorText = result.ErrorText;
                    }
                    else
                    {
                        bytes = GetBytes(content);
                    }

                    PatchedByteArray patchedByteArray = new PatchedByteArray(sector, fileOffset, bytes);
                    patchedByteArray.IsAsm = asmMode;
                    patchedByteArray.AsmText = asmMode ? content : "";
                    patchedByteArray.RamOffset = ramOffset;
                    patchedByteArray.ErrorText = errorText;
                    patchedByteArray.Label = label;
                    
                    patches.Add(patchedByteArray);
                    isDataSectionList.Add(markedAsData);

                    offsetIndex++;
                    if (offsetIndex < strOffsets.Length)
                    {
                        if (isSpecific)
                        {
                            sector = specifics[offsetIndex].Sector;

                            try
                            {
                                ftrOffset = (PsxIso.FileToRamOffsets)Enum.Parse(typeof(PsxIso.FileToRamOffsets), "OFFSET_" + Enum.GetName(typeof(PsxIso.Sectors), sector));
                            }
                            catch (Exception)
                            {
                                ftrOffset = null;
                            }
                        }
                    }
                }
            }

            currentLocs = node.SelectNodes("STRLocation");
            foreach (XmlNode location in currentLocs)
            {
                XmlAttribute fileAttribute = location.Attributes["file"];
                XmlAttribute sectorAttribute = location.Attributes["sector"];
                PsxIso.Sectors sector = (PsxIso.Sectors)0;
                if (fileAttribute != null)
                {
                    sector = (PsxIso.Sectors)Enum.Parse( typeof( PsxIso.Sectors ), fileAttribute.InnerText );
                }
                else if (sectorAttribute != null)
                {
                    sector = (PsxIso.Sectors)Int32.Parse( sectorAttribute.InnerText, System.Globalization.NumberStyles.HexNumber );
                }
                else
                {
                    throw new Exception();
                }

                string filename = location.Attributes["input"].InnerText;
                filename = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( xmlFileName ), filename );

                patches.Add(new STRPatchedByteArray(sector, filename));
            }

            staticPatches = patches.AsReadOnly();
            outDataSectionList = isDataSectionList;
        }

        public static IList<AsmPatch> GetPatches( XmlNode rootNode, string xmlFilename, ASMEncodingUtility asmUtility )
        {
            bool rootHideInDefault = false;
            XmlAttribute attrHideInDefault = rootNode.Attributes["hideInDefault"];
            if (attrHideInDefault != null)
            {
                rootHideInDefault = (attrHideInDefault.InnerText.ToLower().Trim() == "true");
            }

            XmlNodeList patchNodes = rootNode.SelectNodes( "Patch" );
            List<AsmPatch> result = new List<AsmPatch>( patchNodes.Count );
            foreach ( XmlNode node in patchNodes )
            {
                XmlAttribute ignoreNode = node.Attributes["ignore"];
                if ( ignoreNode != null && Boolean.Parse( ignoreNode.InnerText ) )
                    continue;

                string name;
                string description;
                IList<PatchedByteArray> staticPatches;
                List<bool> isDataSectionList;
                bool hideInDefault;

                bool hasDefaultSector = false;
                PsxIso.Sectors defaultSector = (PsxIso.Sectors)0;
                XmlAttribute attrDefaultFile = node.Attributes["file"];
                XmlAttribute attrDefaultSector = node.Attributes["sector"];

                if (attrDefaultFile != null)
                {
                    defaultSector = (PsxIso.Sectors)Enum.Parse(typeof(PsxIso.Sectors), attrDefaultFile.InnerText);
                    hasDefaultSector = true;
                }
                else if (attrDefaultSector != null)
                {
                    defaultSector = (PsxIso.Sectors)Int32.Parse(attrDefaultSector.InnerText, System.Globalization.NumberStyles.HexNumber);
                    hasDefaultSector = true;
                }

                List<VariableType> variables = new List<VariableType>();
                foreach ( XmlNode varNode in node.SelectNodes( "Variable" ) )
                {
                	XmlAttribute numBytesAttr = varNode.Attributes["bytes"];
                    string strNumBytes = (numBytesAttr == null) ? "1" : numBytesAttr.InnerText;
                    char numBytes = (char)(UInt32.Parse(strNumBytes) & 0xff);
                	
                    string varName = varNode.Attributes["name"].InnerText;

                    XmlAttribute fileAttribute = varNode.Attributes["file"];
                    XmlAttribute sectorAttribute = varNode.Attributes["sector"];
                    XmlAttribute attrSpecific = varNode.Attributes["specific"];

                    //PsxIso.Sectors varSec = (PsxIso.Sectors)Enum.Parse( typeof( PsxIso.Sectors ), varNode.Attributes["file"].InnerText );
                    //UInt32 varOffset = UInt32.Parse( varNode.Attributes["offset"].InnerText, System.Globalization.NumberStyles.HexNumber );
                    //string strOffsetAttr = varNode.Attributes["offset"].InnerText;
                    XmlAttribute offsetAttribute = varNode.Attributes["offset"];
                    string strOffsetAttr = (offsetAttribute != null) ? offsetAttribute.InnerText : "";
                    string[] strOffsets = strOffsetAttr.Replace(" ", "").Split(',');
                    bool ignoreOffsetMode = false;
                    bool isSpecific = false;

                    List<SpecificLocation> specifics = FillSpecificAttributeData(attrSpecific, defaultSector);

                    if (specifics.Count > 0)
                    {
                        isSpecific = true;
                        List<string> newStrOffsets = new List<string>(specifics.Count);
                        foreach (SpecificLocation specific in specifics)
                            newStrOffsets.Add(specific.OffsetString);
                        strOffsets = newStrOffsets.ToArray();
                    }
                    else if ((string.IsNullOrEmpty(strOffsetAttr)) && (variables.Count > 0) && (variables[variables.Count - 1].content.Count > 0))
                    {
                        // No offset defined -- offset is (last patch offset) + (last patch size)
                        int lastIndex = variables[variables.Count - 1].content.Count - 1;
                        PatchedByteArray lastPatchedByteArray = variables[variables.Count - 1].content[lastIndex];
                        long offset = lastPatchedByteArray.Offset + lastPatchedByteArray.GetBytes().Length;
                        string strOffset = offset.ToString("X");
                        strOffsets = new string[1] { strOffset };
                        ignoreOffsetMode = true;
                    }

                    PsxIso.Sectors sector = (PsxIso.Sectors)0;
                    if (isSpecific)
                    {
                        sector = specifics[0].Sector;
                    }
                    else if (fileAttribute != null)
                    {
                        sector = (PsxIso.Sectors)Enum.Parse(typeof(PsxIso.Sectors), fileAttribute.InnerText);
                    }
                    else if (sectorAttribute != null)
                    {
                        sector = (PsxIso.Sectors)Int32.Parse(sectorAttribute.InnerText, System.Globalization.NumberStyles.HexNumber);
                    }
                    else if (hasDefaultSector)
                    {
                        sector = defaultSector;
                    }
                    else if ((variables.Count > 0) && (variables[variables.Count - 1].content.Count > 0))
                    {
                        int lastIndex = variables[variables.Count - 1].content.Count - 1;
                        sector = (PsxIso.Sectors)(variables[variables.Count - 1].content[lastIndex].Sector);
                    }
                    else
                    {
                        throw new Exception("Error in patch XML: Invalid file/sector!");
                    }

                    XmlAttribute offsetModeAttribute = varNode.Attributes["offsetMode"];
                    bool isRamOffset = false;
                    if ((!ignoreOffsetMode) && (offsetModeAttribute != null))
                    {
                        if (offsetModeAttribute.InnerText.ToLower().Trim() == "ram")
                            isRamOffset = true;
                    }

                    Nullable<PsxIso.FileToRamOffsets> ftrOffset = null;
                    try
                    {
                        ftrOffset = (PsxIso.FileToRamOffsets)Enum.Parse(typeof(PsxIso.FileToRamOffsets), "OFFSET_" + Enum.GetName(typeof(PsxIso.Sectors), sector));
                        //ftrOffset = (PsxIso.FileToRamOffsets)Enum.Parse(typeof(PsxIso.FileToRamOffsets), "OFFSET_" + fileAttribute.InnerText);
                    }
                    catch (Exception)
                    {
                        ftrOffset = null;
                    }

                    XmlAttribute defaultAttr = varNode.Attributes["default"];
                    Byte[] byteArray = new Byte[numBytes];
                    UInt32 def = 0;
                    if ( defaultAttr != null )
                    {
                        def = UInt32.Parse( defaultAttr.InnerText, System.Globalization.NumberStyles.HexNumber );
                        for (int i = 0; i < numBytes; i++)
                        {
                        	byteArray[i] = (Byte)((def >> (i * 8)) & 0xff);
                        }
                    }

                    List<PatchedByteArray> patchedByteArrayList = new List<PatchedByteArray>();
                    int offsetIndex = 0;

                    foreach (string strOffset in strOffsets)
                    {
                        UInt32 offset = UInt32.Parse(strOffset, System.Globalization.NumberStyles.HexNumber);
                        //UInt32 ramOffset = offset;
                        UInt32 fileOffset = offset;

                        if (ftrOffset.HasValue)
                        {
                            try
                            {
                                if (isRamOffset)
                                    fileOffset -= (UInt32)ftrOffset;
                                //else
                                //    ramOffset += (UInt32)ftrOffset;
                            }
                            catch (Exception) { }
                        }

                        //ramOffset = ramOffset | 0x80000000;     // KSEG0

                        patchedByteArrayList.Add(new PatchedByteArray(sector, fileOffset, byteArray));

                        offsetIndex++;
                        if (offsetIndex < strOffsets.Length)
                        {
                            if (isSpecific)
                            {
                                sector = specifics[offsetIndex].Sector;

                                try
                                {
                                    ftrOffset = (PsxIso.FileToRamOffsets)Enum.Parse(typeof(PsxIso.FileToRamOffsets), "OFFSET_" + Enum.GetName(typeof(PsxIso.Sectors), sector));
                                }
                                catch (Exception)
                                {
                                    ftrOffset = null;
                                }
                            }
                        }
                    }

                    bool isReference = false;
                    string referenceName = "";
                    string referenceOperatorSymbol = "";
                    uint referenceOperand = 0;

                    XmlAttribute attrReference = varNode.Attributes["reference"];
                    XmlAttribute attrOperator = varNode.Attributes["operator"];
                    XmlAttribute attrOperand = varNode.Attributes["operand"];
                    
                    if (attrReference != null)
                    {
                        isReference = true;
                        referenceName = attrReference.InnerText;
                        referenceOperatorSymbol = (attrOperator != null) ? attrOperator.InnerText : "";
                        if (attrOperand != null)
                        {
                            //UInt32.Parse(defaultAttr.InnerText, System.Globalization.NumberStyles.HexNumber);
                            uint.TryParse(attrOperand.InnerText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.CurrentCulture, out referenceOperand);
                        }
                    }

                    VariableType vType = new VariableType();
                    vType.numBytes = numBytes;
                    vType.byteArray = byteArray;
                    vType.name = varName;
                    vType.content = patchedByteArrayList;
                    vType.isReference = isReference;
                    vType.reference = new VariableReference();
                    vType.reference.name = referenceName;
                    vType.reference.operatorSymbol = referenceOperatorSymbol;
                    vType.reference.operand = referenceOperand;

                    variables.Add( vType );
                }

                GetPatch(node, xmlFilename, asmUtility, variables, out name, out description, out staticPatches, out isDataSectionList, out hideInDefault);
                result.Add( new AsmPatch( name, description, staticPatches, isDataSectionList, (hideInDefault | rootHideInDefault), variables ) );
            }

            patchNodes = rootNode.SelectNodes( "ImportFilePatch" );
            foreach ( XmlNode node in patchNodes )
            {
                string name;
                string description;

                GetPatchNameAndDescription(node, out name, out description);

                XmlNodeList fileNodes = node.SelectNodes( "ImportFile" );
                if ( fileNodes.Count != 1 ) continue;

                XmlNode theRealNode = fileNodes[0];

                PsxIso.Sectors sector = (PsxIso.Sectors)Enum.Parse( typeof( PsxIso.Sectors ), theRealNode.Attributes["file"].InnerText );
                UInt32 offset = UInt32.Parse( theRealNode.Attributes["offset"].InnerText, System.Globalization.NumberStyles.HexNumber );
                UInt32 expectedLength = UInt32.Parse( theRealNode.Attributes["expectedLength"].InnerText, System.Globalization.NumberStyles.HexNumber );

                result.Add( new FileAsmPatch( name, description, new InputFilePatch( sector, offset, expectedLength ) ) );

            }

            return result.AsReadOnly();

        }

        private static List<SpecificLocation> FillSpecificAttributeData(XmlAttribute attrSpecific, PsxIso.Sectors defaultSector)
        {
            string strSpecificAttr = (attrSpecific != null) ? attrSpecific.InnerText : "";
            string[] strSpecifics = ASMStringHelper.RemoveSpaces(strSpecificAttr).Split(',');
            List<SpecificLocation> specifics = new List<SpecificLocation>();

            PsxIso.Sectors lastSector = defaultSector;
            if (!string.IsNullOrEmpty(strSpecificAttr))
            {
                foreach (string strSpecific in strSpecifics)
                {
                    string[] strSpecificData = strSpecific.Split(':');

                    string strSector = "";
                    string strSpecificOffset = strSpecificData[0];
                    if (strSpecificData.Length > 1)
                    {
                        strSector = strSpecificData[0];
                        strSpecificOffset = strSpecificData[1];
                    }

                    PsxIso.Sectors specificSector;
                    if (string.IsNullOrEmpty(strSector))
                        specificSector = lastSector;
                    else
                    {
                        int sectorNum = 0;
                        bool isSectorNumeric = int.TryParse(strSector, out sectorNum);
                        specificSector = isSectorNumeric ? (PsxIso.Sectors)sectorNum : (PsxIso.Sectors)Enum.Parse(typeof(PsxIso.Sectors), strSector);
                    }

                    lastSector = specificSector;
                    specifics.Add(new SpecificLocation(specificSector, strSpecificOffset));
                }
            }

            return specifics;
        }

        private static byte[] GetBytes( string byteText )
        {
            string strippedText = stripRegex.Replace( byteText, string.Empty );
    
            int bytes = strippedText.Length / 2;
            byte[] result = new byte[bytes];

            for ( int i = 0; i < bytes; i++ )
            {
                result[i] = Byte.Parse( strippedText.Substring( i * 2, 2 ), System.Globalization.NumberStyles.HexNumber );
            }
            return result;
        }
    }
}
