﻿using System.Runtime.Serialization;

namespace FakeChan22
{
    [DataContract]
    public class ReplaceDefinition
    {
        [DataMember] public bool Apply { get; set; }

        [DataMember] public string MatchingPattern { get; set; }

        [DataMember] public string ReplaceText { get; set; }

        public ReplaceDefinition Clone()
        {
            return (ReplaceDefinition)MemberwiseClone();
        }
    }
}
