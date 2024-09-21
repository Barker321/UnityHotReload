using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace A
{
    public class Test : MonoBehaviour
    {
        private cls a;
        // Start is called before the first frame update
        void Start()
        {

        }

        // Update is called once per frame
        void Update()
        {
            a = new cls();
            a.name = "11";
            UnityEngine.Debug.Log(a.name);
        }
    }
}

namespace A
{
    class cls
    {
        public string name;
    }
}
