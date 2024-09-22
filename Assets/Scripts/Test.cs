using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace A
{
    class Test : MonoBehaviour
    {
        cls a;
        // Start is called before the first frame update
        void Start()
        {

        }

        // Update is called once per frame
        void Update()
        {
            a = new cls();
            a.name = "121";
            UnityEngine.Debug.Log(a.name);
        }
    }
}
