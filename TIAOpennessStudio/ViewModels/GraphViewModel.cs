using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using Prism.Events;
using Prism.Mvvm;
using TIAOpennessStudio.Events;

namespace TIAOpennessStudio.ViewModels
{
    public class GraphViewModel : BindableBase
    {
        private readonly IEventAggregator _eventAggregator;
        
        public GraphViewModel(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }
        
        public void GenerateStepSequence(OneStepCollection steps)
        {
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                string xmlContent = GetResourceTextFile("V14SP1.xml");
                if (string.IsNullOrEmpty(xmlContent))
                {
                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Resource V14SP1.xml not found!");
                    return;
                }
                xmlDoc.LoadXml(xmlContent);
                
                XmlSetAttribute("Engineering", "version", "V14 SP1", xmlDoc);
                
                string dt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                
                XmlSetInnerText("Created", dt, xmlDoc);
                XmlSetInnerText("GraphVersion", "4.0", xmlDoc);
                
                XmlSetInnerText("Name", "FB_SEQ_One", xmlDoc);
                XmlSetInnerText("Number", "1234", xmlDoc);
                
                XmlNodeList intf = xmlDoc.SelectNodes("Document/SW.Blocks.FB/AttributeList/Interface");
                XmlNodeList sections = intf[0].ChildNodes;
                XmlNodeList section = sections[0].ChildNodes;
                
                foreach (XmlNode x in section)
                {
                    if (x.Attributes["Name"] != null)
                    {
                        string aName = (x.Attributes["Name"].Value);
                        if (aName == "Static")
                        {
                            foreach (OneStep st in steps.Steps)
                            {
                                AppendDefiniationTransition(xmlDoc, x, "NextStep (" + st.Number.ToString() + ")", st.Number.ToString());
                                if (st.AbortStep > 0) AppendDefiniationTransition(xmlDoc, x, "AbortStep (" + (st.Number + 100).ToString() + ")", (st.Number + 100).ToString());
                                if (st.OptionStep > 0) AppendDefiniationTransition(xmlDoc, x, "OptionStep (" + (st.Number + 200).ToString() + ")", (st.Number + 200).ToString());
                            }
                            foreach (OneStep st in steps.Steps)
                            {
                                AppendDefinationStep(xmlDoc, x, st.Description, st.Number.ToString());
                            }
                        }
                    }
                }
                
                XmlNode stepList = xmlDoc.SelectSingleNode("Document/SW.Blocks.FB/ObjectList/SW.Blocks.CompileUnit/AttributeList/NetworkSource");
                if (stepList != null && stepList.HasChildNodes)
                {
                    XmlNode gr = stepList.ChildNodes[0];
                    
                    XmlNode seq = XmlGetChild(gr, "Sequence");
                    XmlNode stepsNode = XmlGetChild(seq, "Steps");
                    XmlNode trans = XmlGetChild(seq, "Transitions");
                    XmlNode branch = XmlGetChild(seq, "Branches");
                    XmlNode conn = XmlGetChild(seq, "Connections");
                    
                    foreach (OneStep st in steps.Steps)
                    {
                        AppendStepData(xmlDoc, stepsNode, st);
                    }
                    
                    foreach (OneStep st in steps.Steps)
                    {
                        AppendStepTransition(xmlDoc, trans, "Next Step (" + st.Number.ToString() + ")", st.Number.ToString());
                        if (st.AbortStep > 0) AppendStepTransition(xmlDoc, trans, "Abort Step (" + (st.Number + 100).ToString() + ")", (st.Number + 100).ToString());
                        if (st.OptionStep > 0) AppendStepTransition(xmlDoc, trans, "Option Step (" + (st.Number + 200).ToString() + ")", (st.Number + 200).ToString());
                    }
                    
                    int idx = 0;
                    int branchNo = 0;
                    
                    foreach (OneStep st in steps.Steps)
                    {
                        string source = "StepRef";
                        string sourceValue = st.Number.ToString();
                        string target = "TransitionRef";
                        string targetValue = st.Number.ToString();
                        
                        if ((st.AbortStep == 0) && (st.OptionStep == 0))
                        {
                            AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                            
                            source = "TransitionRef";
                            sourceValue = st.Number.ToString();
                            target = "StepRef";
                            targetValue = st.NextStep.ToString();
                            
                            bool jump = false;
                            
                            if (st.Number == steps.Steps[steps.Steps.Count - 1].Number)
                                jump = true;
                            else
                            {
                                if (idx + 1 < steps.Steps.Count && st.Number == steps.Steps[idx + 1].Number)
                                    jump = true;
                            }
                            
                            AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, jump);
                        }
                        
                        if ((st.AbortStep > 0) || (st.OptionStep > 0))
                        {
                            branchNo++;
                            int outConn = 0;
                            
                            AppendBranch(xmlDoc, branch, st, branchNo);
                            
                            source = "StepRef";
                            sourceValue = st.Number.ToString();
                            target = "BranchRef";
                            targetValue = branchNo.ToString() + "/0";
                            AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                            
                            source = "BranchRef";
                            sourceValue = branchNo.ToString() + "/" + outConn.ToString();
                            target = "TransitionRef";
                            targetValue = st.Number.ToString();
                            AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                            
                            source = "TransitionRef";
                            sourceValue = st.Number.ToString();
                            target = "StepRef";
                            targetValue = st.NextStep.ToString();
                            AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                            
                            if (st.AbortStep != 0)
                            {
                                outConn++;
                                
                                source = "BranchRef";
                                sourceValue = branchNo.ToString() + "/" + outConn; ;
                                target = "TransitionRef";
                                targetValue = (st.Number + 100).ToString();
                                AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                                
                                source = "TransitionRef";
                                sourceValue = (st.Number + 100).ToString();
                                target = "StepRef";
                                targetValue = st.AbortStep.ToString();
                                AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, true);
                            }
                            
                            if (st.OptionStep != 0)
                            {
                                outConn++;
                                
                                source = "BranchRef";
                                sourceValue = branchNo.ToString() + "/" + outConn; ;
                                target = "TransitionRef";
                                targetValue = (st.Number + 200).ToString();
                                AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, false);
                                
                                source = "TransitionRef";
                                sourceValue = (st.Number + 200).ToString();
                                target = "StepRef";
                                targetValue = st.OptionStep.ToString();
                                AppendConnection(xmlDoc, conn, st, source, sourceValue, target, targetValue, true);
                            }
                        }
                        
                        idx++;
                    }
                }
                
                string xml = xmlDoc.OuterXml;
                xml = xml.Replace("xmlns=\"\"", "");
                
                xmlDoc.LoadXml(xml);
                
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Import");
                Directory.CreateDirectory(filePath);
                
                // Trigger the save-file-dialog event
                string defaultPath = Path.Combine(filePath, "Graph.xml");
                _eventAggregator.GetEvent<SaveFileDialogEvent>().Publish(defaultPath);
                
                // Note: the actual save must be done in the View layer; only the XML content is provided here.
                // In the real implementation, the View layer calls the SaveGraph method.
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish(ex.Message);
            }
        }
        
        public void SaveGraph(XmlDocument xmlDoc, string filePath)
        {
            try
            {
                xmlDoc.Save(filePath);
                _eventAggregator.GetEvent<SaveFileCompletedEvent>().Publish(new FileSaveEventArgs(filePath, true, "Graph.xml has been saved"));
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<SaveFileCompletedEvent>().Publish(new FileSaveEventArgs(filePath, false, ex.Message));
            }
        }
        
        public XmlDocument CombineXmlFiles(string file1Path, string file2Path)
        {
            try
            {
                cXmlFile f1 = new cXmlFile(file1Path);
                cXmlFile f2 = new cXmlFile(file2Path);
                
                if (!f1.Valid || !f2.Valid)
                {
                    _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Could not load input XML files for combine.");
                    return null;
                }
                
                f2.ChangeIDs(f1.uidHigh - f1.uidLow + 1,
                             f1.idHigh - f1.idLow + 1);
                
                XmlNode def2 = f2.XmlDocument.SelectSingleNode("//Interface").ChildNodes[0];
                
                foreach (XmlNode aTyp in def2.ChildNodes)
                {
                    foreach (XmlNode aVar in aTyp.ChildNodes)
                    {
                        XmlNode newNode = f1.XmlDocument.ImportNode(aVar, true);
                        AddVarDef(f1.XmlDocument, f2.XmlDocument, aTyp.Attributes["Name"].Value, newNode);
                    }
                }
                
                XmlNode n2 = f2.XmlDocument.SelectSingleNode("//ObjectList");
                foreach (XmlNode x in n2.ChildNodes)
                {
                    if (x.Name.StartsWith("SW.Blocks"))
                    {
                        XmlNode newNode = f1.XmlDocument.ImportNode(x, true);
                        AddSwBlock(f1.XmlDocument, newNode);
                    }
                }
                
                f1.GetLimitIDs();
                return f1.XmlDocument;
            }
            catch (Exception ex)
            {
                _eventAggregator.GetEvent<ShowMessageEvent>().Publish("Error combining files: " + ex.Message);
                return null;
            }
        }
        
        public string GetResourceTextFile(string filename)
        {
            string result = string.Empty;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TIAOpennessStudio.Templates." + filename))
            {
                if (stream != null)
                {
                    using (StreamReader sr = new StreamReader(stream))
                    {
                        result = sr.ReadToEnd();
                    }
                }
            }
            return result;
        }
        
        public void XmlSetAttribute(string name, string attribute, string value, XmlDocument document)
        {
            XmlNodeList f = document.GetElementsByTagName(name);
            if (f.Count == 1)
            {
                XmlAttribute a = f[0].Attributes[attribute];
                if (a != null) a.Value = value;
                else throw new Exception("Error: XmlSetAttribute - Attribute " + name + " not found!");
            }
            else throw new Exception("Error: XmlSetAttribute - Node " + name + " not found!");
        }
        
        public void XmlSetInnerText(string name, string value, XmlDocument document)
        {
            XmlNodeList f = document.GetElementsByTagName(name);
            if (f.Count == 1) f[0].InnerText = value;
            else throw new Exception("Error: XmlSetInnerText - Node " + name + " not found!");
        }
        
        public XmlNode XmlGetChild(XmlNode Node, string Name)
        {
            foreach (XmlNode child in Node.ChildNodes)
            {
                if (child.Name == Name) return child;
            }
            return null;
        }
        
        private void AppendDefiniationTransition(XmlDocument doc, XmlNode node, string name, string value)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("TransitionPlus.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            XmlNodeList f = member.GetElementsByTagName("Member");
            foreach (XmlNode aMember in f)
            {
                if (aMember.Attributes["Name"] != null)
                {
                    aMember.Attributes["Name"].Value = name;
                }
            }
            f = member.GetElementsByTagName("StartValue");
            if (f.Count == 1) f[0].InnerText = value;
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        private void AppendDefinationStep(XmlDocument doc, XmlNode node, string name, string value)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("StepPlus.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            XmlNodeList f = member.GetElementsByTagName("Member");
            if (f.Count > 0 && f[0].Attributes["Name"] != null)
            {
                f[0].Attributes["Name"].Value = name;
            }
            
            f = member.GetElementsByTagName("StartValue");
            if (f.Count > 0) f[0].InnerText = value;
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        private void AppendStepData(XmlDocument doc, XmlNode node, OneStep step)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("Step.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            XmlSetAttribute("Step", "Number", step.Number.ToString(), member);
            XmlSetAttribute("Step", "Name", step.Description, member);
            
            if (step.Number == 1)
            {
                XmlSetAttribute("Step", "Init", "true", member);
            }
            
            XmlSetInnerText("MultiLanguageText", "Action Step " + step.Number.ToString(), member);
            
            XmlNodeList l = member.GetElementsByTagName("Token");
            foreach (XmlNode l1 in l)
            {
                if (l1.OuterXml.Contains("step") && l1.Attributes.Count > 0)
                {
                    l1.Attributes[0].Value = step.Number.ToString();
                }
            }
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        private void AppendStepTransition(XmlDocument doc, XmlNode node, string Name, string Number)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("Transition.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            XmlSetAttribute("Transition", "Number", Number, member);
            XmlSetAttribute("Transition", "Name", Name, member);
            
            XmlNodeList c = member.GetElementsByTagName("ConstantValue");
            foreach (XmlNode l1 in c)
            {
                if (l1.InnerText == "stepNumber")
                {
                    l1.InnerText = Number;
                }
            }
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        private void AppendConnection(XmlDocument doc, XmlNode node, OneStep step, string source, string sourceValue, string target, string targetValue, bool jump)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("Connection.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            XmlNode from = member.SelectSingleNode("Connection/NodeFrom");
            XmlNode to = member.SelectSingleNode("Connection/NodeTo");
            
            string outValue = "";
            if (source == "BranchRef")
            {
                string[] parts = sourceValue.Split('/');
                if (parts.Length > 1)
                {
                    outValue = parts[1];
                    sourceValue = parts[0];
                }
            }
            
            XmlElement chd = member.CreateElement(source);
            XmlAttribute attr = member.CreateAttribute("Number");
            attr.Value = sourceValue;
            chd.Attributes.Append(attr);
            if (source == "BranchRef")
            {
                attr = member.CreateAttribute("Out");
                attr.Value = outValue;
                chd.Attributes.Append(attr);
            }
            from.AppendChild(chd);
            
            outValue = "";
            if (target == "BranchRef")
            {
                string[] parts = targetValue.Split('/');
                if (parts.Length > 1)
                {
                    outValue = parts[1];
                    targetValue = parts[0];
                }
            }
            
            if (targetValue != "100")
            {
                chd = member.CreateElement(target);
                attr = member.CreateAttribute("Number");
                attr.Value = targetValue;
                chd.Attributes.Append(attr);
                if (target == "BranchRef")
                {
                    attr = member.CreateAttribute("In");
                    attr.Value = outValue;
                    chd.Attributes.Append(attr);
                }
                
                to.AppendChild(chd);
            }
            else
            {
                chd = member.CreateElement("EndConnection");
                to.AppendChild(chd);
            }
            
            if (!jump)
                XmlSetInnerText("LinkType", "Direct", member);
            else
                XmlSetInnerText("LinkType", "Jump", member);
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        private void AppendBranch(XmlDocument doc, XmlNode node, OneStep step, int branchNo)
        {
            XmlDocument member = new XmlDocument();
            string xmlContent = GetResourceTextFile("Branch.xml");
            if (string.IsNullOrEmpty(xmlContent)) return;
            member.LoadXml(xmlContent);
            
            int c = 1;
            if (step.AbortStep != 0) c = c + 1;
            if (step.OptionStep != 0) c = c + 1;
            
            XmlSetAttribute("Branch", "Number", branchNo.ToString(), member);
            XmlSetAttribute("Branch", "Cardinality", c.ToString(), member);
            
            XmlNode mNode = doc.ImportNode(member.FirstChild, true);
            node.AppendChild(mNode);
        }
        
        public void AddSwBlock(XmlDocument doc, XmlNode node)
        {
            XmlNode n1 = doc.SelectSingleNode("//ObjectList");
            XmlNode bl = n1.FirstChild;
            
            foreach (XmlNode x in n1.ChildNodes)
            {
                if (x.Name.StartsWith("SW.Blocks"))
                {
                    bl = x;
                }
            }
            
            if (bl != null)
            {
                n1.InsertAfter(node, bl);
            }
        }
        
        public void AddVarDef(XmlDocument Document, XmlDocument MergeDoc, string Name, XmlNode Node)
        {
            XmlNode Section = Document.SelectSingleNode("//Interface").ChildNodes[0];
            
            string member = Node.Attributes["Name"].Value;
            bool exits = false;
            
            foreach (XmlNode aTyp in Section.ChildNodes)
            {
                if (aTyp.Attributes["Name"].Value == Name)
                {
                    foreach (XmlNode aName in aTyp.ChildNodes)
                    {
                        if (aName.Attributes["Name"].Value == member)
                        {
                            exits = true;
                            break;
                        }
                    }
                    
                    if (exits)
                    {
                        XmlNodeList lst = MergeDoc.GetElementsByTagName("Component");
                        
                        string name = Node.Attributes["Name"].Value;
                        string newName = FindNextName(name);
                        
                        Node.Attributes["Name"].Value = newName;
                        
                        foreach (XmlNode x in lst)
                        {
                            if (x.Attributes["Name"].Value == name)
                            {
                                x.Attributes["Name"].Value = newName;
                            }
                        }
                    }
                    aTyp.AppendChild(Node);
                    
                    return;
                }
            }
        }
        
        public string FindNextName(string Name)
        {
            string f = Regex.Match(Name, @"\d+$").ToString();
            if (f != string.Empty)
            {
                int fNo = Convert.ToInt32(f);
                int y = Name.LastIndexOf(f);
                
                return Name.Substring(0, y) + (fNo + 1).ToString();
            }
            else
            {
                return Name + "_1";
            }
        }
        
        public string GetNextFileName(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            
            int i = 0;
            while (File.Exists(fileName))
            {
                if (i == 0)
                    fileName = fileName.Replace(extension, "(" + ++i + ")" + extension);
                else
                    fileName = fileName.Replace("(" + i + ")" + extension, "(" + ++i + ")" + extension);
            }
            return fileName;
        }
    }
    
    public class FileSaveEventArgs : EventArgs
    {
        public string FilePath { get; }
        public bool Success { get; }
        public string Message { get; }
        
        public FileSaveEventArgs(string filePath, bool success, string message)
        {
            FilePath = filePath;
            Success = success;
            Message = message;
        }
    }
}